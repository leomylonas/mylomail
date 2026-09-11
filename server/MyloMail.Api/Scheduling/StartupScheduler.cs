using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Sync;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Rebuilds every outstanding work item at startup and puts it back on the queue (§6).
/// </summary>
/// <remarks>
/// Job storage is in-memory, so nothing survives a restart and this is the <b>only</b>
/// recovery mechanism — not a safety net behind a durable queue. That is why it enumerates
/// the app tables rather than asking the scheduler what was pending: the app tables are the
/// single source of truth, and they win any disagreement.
/// </remarks>
public sealed class StartupScheduler(
	MyloMailDbContext context,
	StartupReconciliation reconciliation,
	SearchIndexer search,
	MessageIngestor ingestor,
	PollRegistry polls,
	Notifications.NotificationService notifications,
	IBackgroundJobClient jobs,
	IHubEvents events,
	IFaultInjector faults,
	ILogger<StartupScheduler> logger
)
{
	public async Task ScheduleAsync(CancellationToken ct = default)
	{
		// A lease held by the process that just died owns nothing now. This says nothing
		// about what the server saw — that is the attempt's business, and an item from an
		// unresolved attempt is reconciled rather than re-executed.
		// Checked at startup and repaired rather than left to fail later. The index is derived
		// data — every term in it comes from content the database still holds — so rebuilding
		// costs time and loses nothing, whereas an inconsistent index surfaces as a write
		// failing with "database disk image is malformed" somewhere unrelated (§8).
		if (!await search.IsIntactAsync(ct))
		{
			logger.LogWarning("The search index was inconsistent with its content and is being rebuilt.");
			await search.RebuildAsync(ct);
		}

		var released = await reconciliation.ReleaseOrphanedLeasesAsync(ct);
		var work = await reconciliation.FindAsync(ct);

		var settings = await context.AppSettings.SingleOrDefaultAsync(ct);
		if (settings is null)
		{
			settings = new AppSettings();
			context.AppSettings.Add(settings);
		}
		if (settings.MessageThreadBackfillVersion < 1)
		{
			var threadAccountIds = await context.Accounts.Select(account => account.Id).ToListAsync(ct);
			foreach (var accountId in threadAccountIds)
			{
				await ingestor.RebuildFallbackThreadsAsync(accountId, ct);
				faults.Reached(FaultPoints.MessageThreadBackfillBeforeCompletion);
			}
			settings.MessageThreadBackfillVersion = 1;
			await context.SaveChangesAsync(ct);
		}

		var accounts = await context
			.Accounts.Where(a => a.IsEnabled && a.AuthState != AuthState.NeedsReauth)
			.Select(a => a.Id)
			.ToListAsync(ct);

		foreach (var accountId in accounts)
		{
			// Topology starts the change-stream loops once it knows the mailboxes. Nothing
			// else can: job storage is in-memory, so after a restart no poll loop exists and
			// live sync would never resume on its own. Claimed here, exactly once, the same
			// way StartCalendarLoop claims CalendarScope: the loop's own self-reschedule at
			// the end of a successful run must go through unguarded, or it would find its own
			// claim already held and deadlock after a single cycle.
			if (polls.TryStart(accountId, SyncJobs.TopologyScope))
			{
				jobs.Enqueue<SyncJobs>(j => j.TopologyAsync(accountId, default));
			}
			jobs.Enqueue<MutationJobs>(j => j.DrainAsync(accountId, default));
			if (work.DraftAccountsNeedingPush.Contains(accountId))
			{
				jobs.Enqueue<DraftJobs>(j => j.PushAsync(accountId, default));
			}
			if (work.PendingCalendarCreationAccounts.Contains(accountId))
			{
				jobs.Enqueue<SyncJobs>(j => j.CalendarCreationRecoveryAsync(accountId, default));
			}

			// A message left Queued/Fetching by the crash has no other path back onto the queue:
			// ContentJobs.FetchNextAsync only self-schedules its own successor while content
			// remains pending and stops rescheduling once drained (§6), so a backlog stalled
			// mid-fetch by a crash would otherwise sit inert until the next live sync page
			// happened to kick the chain again — which an idle IMAP IDLE connection or a fully
			// caught-up account may not do for an arbitrarily long time. FetchNextAsync no-ops
			// harmlessly if nothing is actually pending, the same as DrainAsync/RunAsync above.
			jobs.Enqueue<ContentJobs>(j => j.FetchNextAsync(accountId, default));

			// A standing sweep, not outstanding work interrupted by the crash: tombstone
			// collection is idempotent and re-scans from DB state on every pass, so it needs
			// no persisted "was running" signal — only a restart of the loop (§6).
			jobs.Enqueue<TombstoneGcJobs>(j => j.SweepAsync(accountId, default));

			// Pending sends and unresolved ones both go through the outbox job: it reconciles
			// before it dispatches, so a send left mid-flight by the crash is settled before
			// anything new goes out.
			jobs.Enqueue<OutboxJobs>(j => j.RunAsync(accountId, default));

			if (work.UndeliveredNotificationAccounts.Contains(accountId))
			{
				await notifications.RedispatchPendingAsync(accountId, ct);
			}
		}
		foreach (var accountId in accounts)
			jobs.Enqueue<ContactJobs>(job => job.StartRefreshAsync(accountId));


		foreach (var mailboxId in work.BackfillingMailboxes)
		{
			var accountId = await context
				.Mailboxes.Where(m => m.Id == mailboxId)
				.Select(m => m.AccountId)
				.FirstOrDefaultAsync(ct);

			if (accountId != Guid.Empty)
			{
				jobs.Enqueue<SyncJobs>(j => j.CoveragePageAsync(accountId, mailboxId, default));
			}
		}

		var contactOperations = await context.ContactOperations
			.Where(operation => operation.State == ContactOperationState.Pending
				|| operation.State == ContactOperationState.Dispatched
				|| operation.State == ContactOperationState.Ambiguous)
			.Where(operation => context.Contacts.Any(contact =>
				contact.Id == operation.ContactId && accounts.Contains(contact.AccountId)))
			.ToListAsync(ct);
		foreach (var operation in contactOperations)
		{
			if (operation.State == ContactOperationState.Pending)
			{
				jobs.Enqueue<ContactJobs>(job => job.ExecuteAsync(operation.Id, default));
			}
			else
			{
				jobs.Enqueue<ContactJobs>(job => job.ReconcileAsync(operation.Id, default));
			}
		}

		// Job storage is in-memory, so an export batch mid-walk when the process died is not
		// running anywhere any more — resumed from the durable startup inventory, not restarted
		// from the beginning (§6).
		foreach (var exportId in work.IncompleteExports)
		{
			jobs.Enqueue<ExportJobs>(j => j.RunBatchAsync(exportId, default));
		}

		logger.LogInformation(
			"Startup scheduling: {Accounts} accounts, {Backfills} backfills resumed, {Leases} leases released, "
				+ "{Ambiguous} attempts awaiting reconciliation, {Pending} pending sends, "
				+ "{Unresolved} sends awaiting reconciliation.",
			accounts.Count,
			work.BackfillingMailboxes.Count,
			released,
			work.AmbiguousAttempts.Count,
			work.PendingSends.Count,
			work.UnresolvedSends.Count
		);
	}

	/// <summary>
	/// Clears a reauthenticated account's paused state and immediately requeues its blocked
	/// work, rather than waiting for the next poll (§3).
	/// </summary>
	public async Task ResumeAccountAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		account.AuthState = AuthState.Connected;
		account.LastAuthError = null;
		await context.SaveChangesAsync(ct);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);

		// The poll loops stopped when the account was paused, so their slots are released and
		// topology may start them again.
		polls.StopAll(accountId);

		if (polls.TryStart(accountId, SyncJobs.TopologyScope))
		{
			jobs.Enqueue<SyncJobs>(j => j.TopologyAsync(accountId, default));
		}
		jobs.Enqueue<MutationJobs>(j => j.DrainAsync(accountId, default));
		jobs.Enqueue<OutboxJobs>(j => j.RunAsync(accountId, default));
		// Reauthentication restores provider access to every durable draft save too. The job
		// no-ops when none is dirty, which is preferable to leaving a crash-lost dispatch inert.
		jobs.Enqueue<DraftJobs>(j => j.PushAsync(accountId, default));
		jobs.Enqueue<ContactJobs>(job => job.StartRefreshAsync(accountId));
		jobs.Enqueue<SyncJobs>(j => j.CalendarCreationRecoveryAsync(accountId, default));
		var contactOperations = await context.ContactOperations
			.Where(operation => context.Contacts.Any(contact =>
				contact.Id == operation.ContactId && contact.AccountId == accountId))
			.Where(operation => operation.State == ContactOperationState.Pending
				|| operation.State == ContactOperationState.Dispatched
				|| operation.State == ContactOperationState.Ambiguous)
			.Select(operation => new { operation.Id, operation.State })
			.ToListAsync(ct);
		foreach (var operation in contactOperations)
		{
			if (operation.State == ContactOperationState.Pending)
				jobs.Enqueue<ContactJobs>(job => job.ExecuteAsync(operation.Id, default));
			else
				jobs.Enqueue<ContactJobs>(job => job.ReconcileAsync(operation.Id, default));
		}
	}
}
