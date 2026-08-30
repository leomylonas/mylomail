using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
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
	PollRegistry polls,
	IBackgroundJobClient jobs,
	ILogger<StartupScheduler> logger
)
{
	public async Task ScheduleAsync(CancellationToken ct = default)
	{
		// A lease held by the process that just died owns nothing now. This says nothing
		// about what the server saw — that is the attempt's business, and an item from an
		// unresolved attempt is reconciled rather than re-executed.
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
			// live sync would never resume on its own.
			jobs.Enqueue<SyncJobs>(j => j.TopologyAsync(accountId, default));
			jobs.Enqueue<MutationJobs>(j => j.DrainAsync(accountId, default));
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

		logger.LogInformation(
			"Startup scheduling: {Accounts} accounts, {Backfills} backfills resumed, {Leases} leases released, "
				+ "{Ambiguous} attempts awaiting reconciliation.",
			accounts.Count,
			work.BackfillingMailboxes.Count,
			released,
			work.AmbiguousAttempts.Count
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

		// The poll loops stopped when the account was paused, so their slots are released and
		// topology may start them again.
		polls.StopAll(accountId);

		jobs.Enqueue<SyncJobs>(j => j.TopologyAsync(accountId, default));
		jobs.Enqueue<MutationJobs>(j => j.DrainAsync(accountId, default));
	}
}
