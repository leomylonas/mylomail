using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
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
	IMailProviderFactory providers,
	IBackgroundJobClient jobs,
	ILogger<SyncJobs> logger
)
{
	/// <summary>
	/// The calendar loop is account-scoped, not mailbox-scoped, so it borrows the mail poll
	/// registry with this sentinel rather than a second registry for one extra scope.
	/// </summary>
	private static readonly Guid CalendarScope = Guid.Empty;

	/// <summary>Reconciles an account's mailboxes, then schedules coverage for any that need it.</summary>
	public async Task TopologyAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await RunnableAsync(accountId, ct);
		if (account is null)
		{
			return;
		}

		await GuardAsync(account, () => topology.ReconcileAsync(account, ct), ct);

		var pending = await context
			.Mailboxes.Where(m => m.AccountId == accountId)
			.Where(m => !context.MailboxCoverageStates.Any(c => c.MailboxId == m.Id && c.Status == CoverageStatus.Covered))
			.Select(m => m.Id)
			.ToListAsync(ct);

		foreach (var mailboxId in pending)
		{
			jobs.Enqueue<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default));
		}

		await StartChangeStreamsAsync(account, ct);
		await StartIntegrityReconciliationAsync(account, ct);
		StartCalendarLoop(account);
	}

	/// <summary>
	/// Starts the calendar poll loop if this account has a calendar configured. IMAP alone
	/// never implies one — CalDAV is opt-in, independent configuration (§1).
	/// </summary>
	private void StartCalendarLoop(Account account)
	{
		if (account.ProviderConfig is not ImapProviderConfig { CalDav: not null })
		{
			return;
		}

		if (polls.TryStart(account.Id, CalendarScope))
		{
			jobs.Enqueue<SyncJobs>(j => j.CalendarAsync(account.Id, default));
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
			await GuardAsync(account, () => calendar.SynchronizeAsync(account, ct), ct);
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.CalendarAsync(accountId, default), ex.RetryAfter);
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

		var mailboxIds = await context.Mailboxes.Where(m => m.AccountId == account.Id).Select(m => m.Id).ToListAsync(ct);
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
		var accountScoped =
			providers.For(account).Capabilities.ChangeStreamScope == ChangeStreamScope.Account;

		var mailboxes = await context
			.Mailboxes.Where(m => m.AccountId == account.Id)
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
		if (mailbox is null)
		{
			// Removed by topology reconciliation between this job being enqueued and running.
			return;
		}

		bool more;
		try
		{
			more = await GuardAsync(account, () => coverage.RunPageAsync(account, mailbox, ct: ct), ct);
		}
		catch (ProviderThrottledException ex)
		{
			jobs.Schedule<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default), ex.RetryAfter);
			return;
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
		if (mailbox is null)
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
		catch (Exception)
		{
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
		catch (Exception)
		{
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

		if (account is null || !account.IsEnabled)
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
			return await work();
		}
		catch (ProviderThrottledException ex)
		{
			// Exactly the delay the provider named, for the whole account — not a backoff
			// curve, and not just for this one job.
			gate.Throttle(account.Id, ex.RetryAfter);
			logger.LogWarning("Account {AccountId} throttled for {Delay}.", account.Id, ex.RetryAfter);
			throw;
		}
		catch (ProviderAuthenticationException ex)
		{
			account.AuthState = AuthState.NeedsReauth;
			account.LastAuthError = ex.Message;
			await context.SaveChangesAsync(ct);

			logger.LogWarning("Account {AccountId} needs reauthentication; its jobs are paused.", account.Id);
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
			.Accounts.AnyAsync(a => a.Id == accountId && a.IsEnabled && a.AuthState != AuthState.NeedsReauth, ct);

		if (!runnable)
		{
			logger.LogInformation("Account {AccountId} stopped being runnable while its job ran.", accountId);
			polls.StopAll(accountId);
			integrityLoops.StopAll(accountId);
		}

		return runnable;
	}
}
