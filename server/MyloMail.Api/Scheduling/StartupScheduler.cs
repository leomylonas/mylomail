using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;

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
	PollRegistry polls,
	Notifications.NotificationService notifications,
	IBackgroundJobClient jobs,
	IHubEvents events,
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

			// A standing sweep, not outstanding work interrupted by the crash: tombstone
			// collection is idempotent and re-scans from DB state on every pass, so it needs
			// no persisted "was running" signal — only a restart of the loop (§6).
			jobs.Enqueue<TombstoneGcJobs>(j => j.SweepAsync(accountId, default));

			// Pending sends and unresolved ones both go through the outbox job: it reconciles
			// before it dispatches, so a send left mid-flight by the crash is settled before
			// anything new goes out.
			jobs.Enqueue<OutboxJobs>(j => j.RunAsync(accountId, default));

			// A notification recorded but not yet confirmed delivered may never have reached
			// the OS — the crash could have landed on either side of that gap. Re-announcing
			// it is the recovery path §13 Epic 9 calls for: a possible duplicate, never a
			// silently dropped notification.
			await notifications.RedispatchPendingAsync(accountId, ct);
		}

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

		// Job storage is in-memory, so an export batch mid-walk when the process died is not
		// running anywhere any more — resumed from its own persisted progress (§6 table), not
		// restarted from the beginning.
		var exports = await context
			.ExportJobs.Where(j => j.Status == ExportJobStatus.Running)
			.Select(j => j.Id)
			.ToListAsync(ct);

		foreach (var exportId in exports)
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
	}
}
