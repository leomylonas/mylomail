using System.Collections.Concurrent;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// The jobs that drive sync and mutation (§3, §6).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every job here is <c>[AutomaticRetry(Attempts = 0)]</c>, and that is not an oversight.</b>
/// Retry is decided here, not by Hangfire: an auth failure must not retry at all, and a
/// throttling response must wait exactly the delay the provider named. Hangfire's own retry
/// curve would fire underneath both — retrying the auth failure that must not be retried, and
/// retrying the throttled call before its window, on top of the reschedule this code already
/// made.
/// </para>
/// <para>
/// Recurring work is self-scheduling rather than registered with Hangfire's recurring
/// scheduler, which is minute-granular and cannot express sub-minute polling. Each run
/// enqueues its own successor, which also keeps job state bounded: work is enqueued
/// incrementally rather than fanned out up front (§6).
/// </para>
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class SyncJobs(
	MyloMailDbContext context,
	TopologySyncService topology,
	CoverageService coverage,
	ChangeStreamService changes,
	IntegrityReconciliationService integrity,
	CalendarSyncService calendar,
	AccountGate gate,
	PollRegistry polls,
	IntegrityRegistry integrityLoops,
	ImapIdleWakeRegistry idleWakes,
	IMailProviderFactory providers,
	IBackgroundJobClient jobs,
	IHubEvents events,
	ILogger<SyncJobs> logger
)
{
	/// <summary>
	/// The first retry delay after a network-class failure (§ Offline behaviour), and the
	/// base §1091's "falls back to the existing exponential backoff" doubles from — short
	/// enough that connectivity returning is noticed promptly on the very first retry.
	/// </summary>
	private static readonly TimeSpan NetworkRetryBaseDelay = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Doubling stops here: a genuinely offline machine settles into checking every 30 minutes
	/// rather than hammering a dead socket, but also rather than backing off indefinitely —
	/// this is a poll loop that must resume promptly once connectivity returns, not a one-shot
	/// retry that can afford to wait longer the more times it has already failed.
	/// </summary>
	private static readonly TimeSpan NetworkRetryMaxDelay = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Consecutive network-class failures per account, across every poll loop that account
	/// runs — reset to zero the moment any of that account's provider calls succeeds again in
	/// <see cref="GuardAsync{T}"/>. Deliberately per-account, not per-job-kind: these failures
	/// share one underlying transport (the same provider, the same network path), so an
	/// independent streak per loop would just mean some loops retry faster than others for the
	/// same outage. In-memory only, like <see cref="AccountGate"/>'s own throttle state — a
	/// restart naturally starts every account back at the fastest retry, which is correct, not
	/// a state loss to guard against.
	/// </summary>
	private static readonly ConcurrentDictionary<Guid, int> networkFailureStreak = new();

	/// <summary>
	/// §1091's fallback exponential backoff for a network-class failure with no explicit
	/// provider signal to honour (unlike <see cref="ProviderThrottledException"/>, which always
	/// uses its own exact <c>RetryAfter</c> instead of this).
	/// </summary>
	internal static TimeSpan NextNetworkRetryDelay(Guid accountId)
	{
		var streak = networkFailureStreak.AddOrUpdate(accountId, 1, (_, previous) => previous + 1);
		var delay = NetworkRetryBaseDelay * Math.Pow(2, streak - 1);
		return delay < NetworkRetryMaxDelay ? delay : NetworkRetryMaxDelay;
	}

	/// <summary>
	/// The calendar loop is account-scoped, not mailbox-scoped, so it borrows the mail poll
	/// registry with this sentinel rather than a second registry for one extra scope.
	/// </summary>
	private static readonly Guid CalendarScope = Guid.Empty;

	/// <summary>
	/// Topology is also account-scoped; it borrows the same registry under a second, distinct
	/// sentinel so its own loop's start/stop bookkeeping does not collide with
	/// <see cref="CalendarScope"/>'s. Internal, not private: <see cref="StartupScheduler"/> and
	/// <see cref="Hubs.MailHub"/>'s <c>UpdateAccount</c> are this loop's external starters — the
	/// same relationship <see cref="CalendarScope"/> has with <c>StartCalendarLoop</c> — and
	/// each must claim the scope itself before enqueuing a run, since the loop's own
	/// self-reschedule at the bottom of a successful run must go through unguarded (a
	/// self-reschedule while still holding its own claim would find that claim already taken
	/// and deadlock after one cycle).
	/// </summary>
	internal static readonly Guid TopologyScope = new("11111111-1111-1111-1111-111111111111");

	/// <summary>
	/// Reconciles an account's mailboxes, schedules coverage for any that need it, and
	/// reschedules itself at the account's poll interval.
	/// </summary>
	/// <remarks>
	/// A folder created, renamed or deleted elsewhere is invisible to every other loop here —
	/// this class's own doc comment says each provider "reconciles topology separately and on
	/// its own cadence," but until this fix the only call to <see cref="TopologySyncService
	/// .ReconcileAsync"/> ran once at startup (or account resume) and never rescheduled, unlike
	/// every sibling loop (<see cref="ChangeStreamAsync"/>, <see cref="IntegrityAsync"/>,
	/// <see cref="CalendarAsync"/>) which all self-reschedule. A folder created mid-session in
	/// another client would go undiscovered until the app next restarted.
	/// </remarks>
	public async Task TopologyAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		if (account is null)
		{
			polls.Stop(accountId, TopologyScope);
			return;
		}

		List<Guid> pending;
		Guid? changeStreamMailboxId = null;
		var changeStreamTopologyGeneration = 0;
		var runningChangeStream = false;
		try
		{
			await GuardAsync(account, () => topology.ReconcileAsync(account, ct), ct);

			pending = await context
				.Mailboxes.Where(m => m.AccountId == accountId && m.ProviderMailboxId != null)
				.Where(m => !context.MailboxCoverageStates.Any(c => c.MailboxId == m.Id && c.Status == CoverageStatus.Covered))
				.Select(m => m.Id)
				.ToListAsync(ct);
			var hasGmailBaseline = account.ProviderType == ProviderType.Gmail
				&& await context.ChangeStreamStates.AnyAsync(
					state => state.AccountId == accountId
						&& state.MailboxId == null
						&& state.CursorState != null
						&& !state.IsRebasing,
					ct
				);
			var needsGmailBaseline = account.ProviderType == ProviderType.Gmail
				&& (!hasGmailBaseline && pending.Count > 0
					|| await context.ChangeStreamStates.AnyAsync(
						state => state.AccountId == accountId && state.MailboxId == null && state.IsRebasing,
						ct
					));
			if (needsGmailBaseline)
			{
				var streamMailbox = await context
					.Mailboxes.Where(m => m.AccountId == accountId && m.ProviderMailboxId != null)
					.OrderBy(m => m.SpecialUse == SpecialUse.Inbox ? 0 : 1)
					.ThenBy(m => m.Id)
					.FirstAsync(ct);
				changeStreamMailboxId = streamMailbox.Id;
				changeStreamTopologyGeneration = streamMailbox.TopologyGeneration;
				runningChangeStream = true;
				await GuardAsync(account, () => changes.SyncAsync(account, streamMailbox, ct), ct);
				runningChangeStream = false;
				pending = await context
					.Mailboxes.Where(m => m.AccountId == accountId && m.ProviderMailboxId != null)
					.Where(m => !context.MailboxCoverageStates.Any(c => c.MailboxId == m.Id && c.Status == CoverageStatus.Covered))
					.Select(m => m.Id)
					.ToListAsync(ct);
			}
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.TopologyAsync(accountId, default), ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			// Quietly retried rather than surfaced as a fresh job failure every offline poll
			// (§ Offline behaviour) — the poll loop stays alive so it resumes on its own once
			// connectivity returns, instead of needing something else to restart it.
			jobs.Schedule<SyncJobs>(j => j.TopologyAsync(accountId, default), NextNetworkRetryDelay(accountId));
			return;
		}
		catch (Exception ex) when (ex is not SimulatedCrashException)
		{
			if (runningChangeStream && changeStreamMailboxId is Guid streamMailboxId)
			{
				await changes.RecordFailureAsync(
					accountId,
					streamMailboxId,
					changeStreamTopologyGeneration,
					ex,
					ct
				);
			}
			else
			{
				await topology.RecordFailureAsync(accountId, ex, ct);
			}
			polls.Stop(accountId, TopologyScope);
			throw;
		}


		foreach (var mailboxId in pending)
		{
			jobs.Enqueue<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default));
		}

		await StartChangeStreamsAsync(account, ct);
		await StartIntegrityReconciliationAsync(account, ct);
		StartCalendarLoop(account);

		if (!await StillRunnableAsync(accountId, ct))
		{
			polls.Stop(accountId, TopologyScope);
			return;
		}

		jobs.Schedule<SyncJobs>(j => j.TopologyAsync(accountId, default), PollInterval(account) + gate.Delay(accountId));
	}

	/// <summary>
	/// Starts one calendar poll loop for every native-calendar account. IMAP alone never implies
	/// one — its CalDAV endpoint remains explicit configuration (§1); Gmail and Microsoft 365
	/// calendar access is native to their already-configured provider accounts.
	/// </summary>
	private void StartCalendarLoop(Account account)
	{
		if (account.ProviderType == ProviderType.Imap
			&& account.ProviderConfig is not ImapProviderConfig { CalDav: not null })
		{
			return;
		}

		if (polls.TryStart(account.Id, CalendarScope))
		{
			jobs.Enqueue<SyncJobs>(j => j.CalendarAsync(account.Id, default));
		}
	}

	/// <summary>
	/// Recovers a calendar create that was dispatched before the process stopped. This
	/// deliberately ignores <see cref="Account.PollingEnabled"/>: pausing periodic sync must
	/// not turn an ambiguous provider create into permanent orphaned work.
	/// </summary>
	public async Task CalendarCreationRecoveryAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(
			row => row.Id == accountId && row.IsEnabled && row.AuthState != AuthState.NeedsReauth,
			ct
		);
		if (account is null)
		{
			return;
		}

		try
		{
			await GuardAsync(account, () => calendar.RecoverPendingCreationsOnlyAsync(account, ct), ct);
			if (await context.CalendarCreationAttempts
				.Join(context.Calendars, attempt => attempt.CalendarId, calendar => calendar.Id, (attempt, calendar) => new { attempt, calendar })
				.AnyAsync(item => item.calendar.AccountId == accountId && !item.calendar.IsLocalOnly, ct))
			{
				jobs.Schedule<SyncJobs>(j => j.CalendarCreationRecoveryAsync(accountId, default), TimeSpan.FromMinutes(5));
			}
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.CalendarCreationRecoveryAsync(accountId, default), ex.RetryAfter);
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			jobs.Schedule<SyncJobs>(j => j.CalendarCreationRecoveryAsync(accountId, default), NextNetworkRetryDelay(accountId));
		}
		catch (Credentials.CredentialStoreUnavailableException)
		{
			jobs.Schedule<SyncJobs>(j => j.CalendarCreationRecoveryAsync(accountId, default), NextNetworkRetryDelay(accountId));
		}
	}

	/// <summary>One calendar sync run, rescheduling itself at the account's poll interval.</summary>
	public async Task CalendarAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		if (account is null)
		{
			polls.Stop(accountId, CalendarScope);
			return;
		}

		try
		{
			await GuardAsync(account, () => calendar.SynchronizeAsync(account, ct: ct), ct);
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.CalendarAsync(accountId, default), ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			jobs.Schedule<SyncJobs>(j => j.CalendarAsync(accountId, default), NextNetworkRetryDelay(accountId));
			return;
		}
		catch (Exception)
		{
			polls.Stop(accountId, CalendarScope);
			throw;
		}

		if (!await StillRunnableAsync(accountId, ct))
		{
			polls.Stop(accountId, CalendarScope);
			return;
		}

		jobs.Schedule<SyncJobs>(j => j.CalendarAsync(accountId, default), PollInterval(account) + gate.Delay(accountId));
	}

	/// <summary>Starts the slower degraded-IMAP maintenance loop, once per mailbox.</summary>
	public async Task StartIntegrityReconciliationAsync(Account account, CancellationToken ct = default)
	{
		if (!await integrity.RequiredAsync(account, ct))
		{
			return;
		}

		var mailboxIds = await context
			.Mailboxes.Where(m => m.AccountId == account.Id && m.ProviderMailboxId != null)
			.Select(m => m.Id)
			.ToListAsync(ct);
		foreach (var mailboxId in mailboxIds)
		{
			if (integrityLoops.TryStart(account.Id, mailboxId))
			{
				jobs.Enqueue<SyncJobs>(j => j.IntegrityAsync(account.Id, mailboxId, default));
			}
		}
	}

	/// <summary>
	/// Starts the change-stream poll loops for an account, one per stream — which is not the
	/// same as one per mailbox.
	/// </summary>
	/// <remarks>
	/// Gmail's stream is account-scoped, so it gets exactly one loop however many labels the
	/// account has. Starting one per label would consume the same history stream repeatedly
	/// and race between the jobs, which is the fiction §1 rejects.
	/// </remarks>
	public async Task StartChangeStreamsAsync(Account account, CancellationToken ct = default)
	{
		var capabilities = providers.For(account).Capabilities;
		var accountScoped = capabilities.ChangeStreamScope == ChangeStreamScope.Account;
		var mailboxQuery = context.Mailboxes.Where(m =>
			m.AccountId == account.Id && m.ProviderMailboxId != null
		);
		if (capabilities.RequiresCoverageBeforeInitialChangeStream)
		{
			mailboxQuery = mailboxQuery.Where(m =>
				context.MailboxCoverageStates.Any(coverage =>
					coverage.MailboxId == m.Id && coverage.Status == CoverageStatus.Covered
				)
				|| context.ChangeStreamStates.Any(state =>
					state.AccountId == account.Id
					&& state.MailboxId == m.Id
					&& state.CursorState != null
					&& !state.IsRebasing
				)
			);
		}

		var mailboxes = await mailboxQuery
			.OrderBy(m => m.SpecialUse == SpecialUse.Inbox ? 0 : 1)
			.ThenBy(m => m.Id)
			.Select(m => m.Id)
			.ToListAsync(ct);

		var scopes = accountScoped ? mailboxes.Take(1) : mailboxes;

		foreach (var mailboxId in scopes)
		{
			// Only one loop per scope: each run enqueues its own successor, so starting a
			// second doubles the poll rate, and topology reconciliation runs repeatedly.
			if (polls.TryStart(account.Id, mailboxId))
			{
				jobs.Enqueue<SyncJobs>(j => j.ChangeStreamAsync(account.Id, mailboxId, default));
			}
		}
	}

	/// <summary>
	/// Fetches one coverage page and enqueues its own successor while more remain.
	/// </summary>
	/// <remarks>
	/// One page per job, not a loop inside one job: a long backfill must survive a restart at
	/// page granularity, and the resume token committed with each page is what it resumes
	/// from.
	/// </remarks>
	public async Task CoveragePageAsync(Guid accountId, Guid mailboxId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		if (account is null)
		{
			return;
		}

		var mailbox = await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct);
		if (mailbox is null || mailbox.ProviderMailboxId is null)
		{
			// Removed by topology reconciliation between this job being enqueued and running.
			return;
		}

		bool more;
		try
		{
			more = await GuardAsync(account, () => coverage.RunPageAsync(account, mailbox, ct: ct), ct);
		}
		catch (CoverageBaselinePendingException)
		{
			// Cursor invalidation queues its owning change-stream loop's topology restart.
			// A coverage job must not create competing account-scoped stream loops.
			return;
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default), ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			jobs.Schedule<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default), NextNetworkRetryDelay(accountId));
			return;
		}
		catch (Exception ex) when (ex is not SimulatedCrashException)
		{
			await coverage.RecordFailureAsync(
				accountId,
				mailboxId,
				mailbox.TopologyGeneration,
				mailbox.CoveragePolicyGeneration,
				ex,
				ct
			);
			throw;
		}

		if (!await StillRunnableAsync(accountId, ct))
		{
			return;
		}

		if (more)
		{
			jobs.Enqueue<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default));
			return;
		}

		await StartChangeStreamsAsync(account, ct);

		// Coverage for this mailbox is done; Gmail's staged history may now be replayable.
		jobs.Enqueue<SyncJobs>(j => j.ReplayStagedAsync(accountId, default));

		// Content last, deliberately: an account becomes usable when its metadata lands, and
		// bodies are what make it searchable afterwards (§1).
		jobs.Enqueue<ContentJobs>(j => j.FetchNextAsync(accountId, default));
	}

	/// <summary>One change-stream run for one mailbox, rescheduling itself at the account's poll interval.</summary>
	public async Task ChangeStreamAsync(Guid accountId, Guid mailboxId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		if (account is null)
		{
			// Disabled, paused or throttled. The loop ends and releases its slot; whatever
			// unpauses the account starts it again, so a paused account does not keep a
			// job spinning against a gate.
			polls.Stop(accountId, mailboxId);
			return;
		}

		var mailbox = await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct);
		if (mailbox is null || mailbox.ProviderMailboxId is null)
		{
			polls.Stop(accountId, mailboxId);
			return;
		}
		var hasEstablishedCursor = await context.ChangeStreamStates.AnyAsync(
			state =>
				state.AccountId == accountId
				&& state.MailboxId == mailboxId
				&& state.CursorState != null
				&& !state.IsRebasing,
			ct
		);
		if (
			providers.For(account).Capabilities.RequiresCoverageBeforeInitialChangeStream
			&& !hasEstablishedCursor
			&& !await context.MailboxCoverageStates.AnyAsync(
				coverage =>
					coverage.MailboxId == mailboxId
					&& coverage.Status == CoverageStatus.Covered,
				ct
			)
		)
		{
			polls.Stop(accountId, mailboxId);
			return;
		}

		try
		{
			await GuardAsync(account, () => changes.SyncAsync(account, mailbox, ct), ct);
		}
		catch (ProviderThrottledException ex)
		{
			// Rescheduled at exactly the delay the provider named. Without this the loop
			// would simply end here, since retry is disabled — and live sync for this scope
			// would never resume.
			jobs.Schedule<SyncJobs>(j => j.ChangeStreamAsync(accountId, mailboxId, default), ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			jobs.Schedule<SyncJobs>(j => j.ChangeStreamAsync(accountId, mailboxId, default), NextNetworkRetryDelay(accountId));
			return;
		}
		catch (Exception ex) when (ex is not SimulatedCrashException)
		{
			await changes.RecordFailureAsync(
				accountId,
				mailboxId,
				mailbox.TopologyGeneration,
				ex,
				ct
			);
			polls.Stop(accountId, mailboxId);
			throw;
		}


		if (!await StillRunnableAsync(accountId, ct))
		{
			// Disabled while this run was in flight: stop rather than enqueue a successor for
			// an account that is going away.
			polls.Stop(accountId, mailboxId);
			return;
		}

		jobs.Schedule<SyncJobs>(
			j => j.ChangeStreamAsync(accountId, mailboxId, default),
			PollInterval(account) + gate.Delay(accountId)
		);
	}

	/// <summary>
	/// Consumes one non-authoritative IMAP IDLE wakeup without claiming or extending the
	/// self-scheduling poll loop. The regular loop remains the sole owner of its registry slot
	/// and successor scheduling.
	/// </summary>
	public async Task WakeChangeStreamAsync(
		Guid accountId,
		Guid mailboxId,
		CancellationToken ct = default
	)
	{
		try
		{
			var account = await RunnableAsync(accountId, ct);
			var mailbox = await context.Mailboxes.FirstOrDefaultAsync(
				candidate => candidate.Id == mailboxId,
				ct
			);
			if (account is null || mailbox is null) return;

			try
			{
				await GuardAsync(account, () => changes.SyncAsync(account, mailbox, ct), ct);
			}
			catch (ProviderThrottledException)
			{
				// The account gate is set by GuardAsync. The existing poll loop will resume at its
				// normal ownership boundary once the provider's exact delay has elapsed.
			}
			catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
			{
				// IDLE is only a latency hint. A failed hint must not create a second retry loop.
			}
		}
		finally
		{
			if (idleWakes.Complete((accountId, mailboxId)))
			{
				try
				{
					jobs.Enqueue<SyncJobs>(job =>
						job.WakeChangeStreamAsync(accountId, mailboxId, default));
				}
				catch
				{
					idleWakes.Release((accountId, mailboxId));
					throw;
				}
			}
		}
	}

	/// <summary>Replays staged history once coverage allows it.</summary>
	public async Task ReplayStagedAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		if (account is null)
		{
			return;
		}

		await GuardAsync(account, () => changes.ReplayStagedAsync(account, ct), ct);
	}

	/// <summary>
	/// Reconciles IMAP UID membership, and basic-tier flags, on a cadence even with a valid
	/// cursor. This is not triggered resync and never resets the cursor.
	/// </summary>
	public async Task IntegrityAsync(Guid accountId, Guid mailboxId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		var mailbox = await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct);
		if (account is null || mailbox is null)
		{
			integrityLoops.Stop(accountId, mailboxId);
			return;
		}

		try
		{
			await GuardAsync(account, () => integrity.ReconcileAsync(account, mailbox, ct), ct);
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.IntegrityAsync(accountId, mailboxId, default), ex.RetryAfter);
			return;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			jobs.Schedule<SyncJobs>(j => j.IntegrityAsync(accountId, mailboxId, default), NextNetworkRetryDelay(accountId));
			return;
		}
		catch (Exception ex) when (ex is not SimulatedCrashException)
		{
			await integrity.RecordFailureAsync(
				accountId,
				mailboxId,
				mailbox.TopologyGeneration,
				ex,
				ct
			);
			integrityLoops.Stop(accountId, mailboxId);
			throw;
		}

		if (!await StillRunnableAsync(accountId, ct))
		{
			integrityLoops.Stop(accountId, mailboxId);
			return;
		}

		jobs.Schedule<SyncJobs>(
			j => j.IntegrityAsync(accountId, mailboxId, default),
			TimeSpan.FromMinutes(30) + gate.Delay(accountId)
		);
	}

	private TimeSpan PollInterval(Account account) =>
		TimeSpan.FromSeconds(Math.Max(account.PollIntervalSeconds, 1));

	/// <summary>
	/// Loads the account if its jobs may run at all.
	/// </summary>
	/// <remarks>
	/// <c>IsEnabled</c> is checked at entry and again after the work, so a worker that was
	/// already running when an account was removed cannot commit for it. This does not make
	/// every job harmless — a provider call already in flight may still complete remotely —
	/// but it prevents further local writes and further enqueues (§3).
	/// </remarks>
	private async Task<Account?> RunnableAsync(Guid accountId, CancellationToken ct)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);

		if (account is null || !account.IsEnabled || !account.PollingEnabled)
		{
			return null;
		}

		if (account.AuthState == AuthState.NeedsReauth)
		{
			// Paused until the user reauthenticates. ReauthenticateAccount requeues the work.
			return null;
		}

		var delay = gate.Delay(accountId);
		if (delay > TimeSpan.Zero)
		{
			logger.LogInformation("Account {AccountId} is throttled for a further {Delay}.", accountId, delay);
			return null;
		}

		return account;
	}

	/// <summary>
	/// Runs provider work, translating the two failures that must not be retried blindly into
	/// the responses §3 requires.
	/// </summary>
	private async Task<T> GuardAsync<T>(Account account, Func<Task<T>> work, CancellationToken ct)
	{
		try
		{
			var result = await work();
			// The provider call just succeeded, so whatever streak of network-class failures
			// this account had built up no longer reflects reality — the next failure (if any)
			// should retry promptly again, not inherit a stale backoff from an outage that's
			// already over.
			networkFailureStreak.TryRemove(account.Id, out _);
			if (account.AuthState == AuthState.CredentialStoreUnavailable)
			{
				// Unlike NeedsReauth, this state is never gated off from retrying (see the
				// catch below) — so a later attempt succeeding, once the OS store is reachable
				// again, is the recovery path itself, not something the user resolved through
				// the UI. Self-clearing here avoids leaving a stale banner up after the real
				// problem is already gone.
				account.AuthState = AuthState.Connected;
				account.LastAuthError = null;
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
			}
			return result;
		}
		catch (ProviderThrottledException ex)
		{
			// Exactly the delay the provider named, for the whole account — not a backoff
			// curve, and not just for this one job.
			gate.Throttle(account.Id, ex.RetryAfter);
			logger.LogWarning("Account {AccountId} throttled for {Delay}.", account.Id, ex.RetryAfter);
			// A quiet diagnostic signal only (§ no user-facing error for something that's
			// already retrying itself automatically) — fires once when the gate first engages
			// for this account, not on every job that finds it already closed (those never
			// reach the provider at all; see RunnableAsync's own gate.Delay check above), so
			// this does not repeat per retry.
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct, gate);
			throw;
		}
		catch (ProviderAuthenticationException ex)
		{
			account.AuthState = AuthState.NeedsReauth;
			account.LastAuthError = ex.Message;
			await context.SaveChangesAsync(ct);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);

			logger.LogWarning("Account {AccountId} needs reauthentication; its jobs are paused.", account.Id);
			throw;
		}
		catch (Credentials.CredentialStoreUnavailableException ex)
		{
			// Not NeedsReauth: the stored credential is not necessarily wrong, the OS store
			// itself could not be reached (a locked keyring, a denied Keychain prompt). Jobs
			// are deliberately left unpaused — this costs nothing against the provider, unlike
			// a bad-password retry, and the store commonly becomes reachable again on its own
			// (the user unlocks their session), so the next scheduled attempt is the recovery
			// path rather than something the user must act on here.
			account.AuthState = AuthState.CredentialStoreUnavailable;
			account.LastAuthError = ex.Message;
			await context.SaveChangesAsync(ct);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);

			logger.LogWarning("Account {AccountId}'s credential store could not be reached.", account.Id);
			throw;
		}
	}

	private Task GuardAsync(Account account, Func<Task> work, CancellationToken ct) =>
		GuardAsync(
			account,
			async () =>
			{
				await work();
				return true;
			},
			ct
		);

	/// <summary>
	/// The exit half of §3's account-removal rule, checked before anything is enqueued.
	/// </summary>
	/// <remarks>
	/// <b>This bounds what happens next; it does not undo what already happened.</b> A
	/// provider call already in flight may still complete remotely, and §3 says so plainly:
	/// removal prevents further local commits and further enqueues, and is not a claim that
	/// the account was cleanly severed. An earlier version of this check only logged, which
	/// made the guarantee decorative.
	/// </remarks>
	private async Task<bool> StillRunnableAsync(Guid accountId, CancellationToken ct)
	{
		var runnable = await context
			.Accounts.AnyAsync(
				a => a.Id == accountId && a.IsEnabled && a.PollingEnabled && a.AuthState != AuthState.NeedsReauth,
				ct
			);

		if (!runnable)
		{
			logger.LogInformation("Account {AccountId} stopped being runnable while its job ran.", accountId);
			polls.StopAll(accountId);
			integrityLoops.StopAll(accountId);
		}

		return runnable;
	}
}
