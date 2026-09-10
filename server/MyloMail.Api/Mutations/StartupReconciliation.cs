using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Outstanding work found in the app tables at startup (§6).
/// </summary>
/// <remarks>
/// With in-memory job storage this is <b>the</b> recovery mechanism, not a safety net behind
/// a durable queue: nothing else knows what was in flight when the process died. It is
/// therefore enumerated explicitly and tested directly rather than assumed correct.
/// </remarks>
public sealed record OutstandingWork(
	IReadOnlyList<Guid> BackfillingMailboxes,
	IReadOnlyList<Guid> NonTerminalMutationChains,
	IReadOnlyList<Guid> AmbiguousAttempts,
	IReadOnlyList<Guid> UnfetchedContentMessages,
	IReadOnlyList<Guid> PendingSends,
	IReadOnlyList<Guid> UnresolvedSends,
	IReadOnlyList<Guid> IncompleteExports,
	IReadOnlyList<Guid> DraftAccountsNeedingPush,
	IReadOnlyList<Guid> PendingCalendarCreationAccounts,
	IReadOnlyList<Guid> UndeliveredNotificationAccounts
);

public sealed class StartupReconciliation(MyloMailDbContext context, TimeProvider clock, ILogger<StartupReconciliation> logger)
{
	/// <summary>
	/// Enumerates every durable source of outstanding work. Job storage is in-memory, so an
	/// omitted table would become permanently inert after a process restart.
	/// </summary>
	public async Task<OutstandingWork> FindAsync(CancellationToken ct = default)
	{
		var backfilling = await context
			.MailboxCoverageStates.Where(c => c.Status == CoverageStatus.Backfilling)
			.Select(c => c.MailboxId)
			.ToListAsync(ct);

		var chains = await context
			.MutationItems.Where(m =>
				m.State != MutationState.Completed
				&& m.State != MutationState.Failed
				&& m.State != MutationState.Cancelled
			)
			.Select(m => m.Id)
			.ToListAsync(ct);

		// Dispatched without persisted results. These route to reconciliation and must never
		// be blindly retried: the provider may well have applied them.
		var ambiguous = await context
			.MutationExecutionAttempts.Where(a =>
				a.State == MutationAttemptState.Dispatched || a.State == MutationAttemptState.Ambiguous
			)
			.Select(a => a.Id)
			.ToListAsync(ct);

		var content = await context
			.MessageContentStates.Where(c =>
				c.Status == ContentStatus.Queued || c.Status == ContentStatus.Fetching
			)
			.Select(c => c.MessageId)
			.ToListAsync(ct);

		// Compared in memory: SQLite does not order or compare DateTimeOffset values reliably.
		// A durable draft whose last push predates its saved revision is outstanding work just as
		// a queued outbox item is; in-memory Hangfire has no other way to recover it.
		var draftAccounts = (
			await context.Drafts.Where(draft => !draft.SyncConflict).ToListAsync(ct)
		)
			.Where(draft => draft.PushedAt is null || draft.PushedAt < draft.SavedAt)
			.Select(draft => draft.AccountId)
			.Distinct()
			.ToList();

		var calendarCreationAccounts = await (
			from attempt in context.CalendarCreationAttempts
			join calendar in context.Calendars on attempt.CalendarId equals calendar.Id
			select calendar.AccountId
		).Distinct().ToListAsync(ct);

		// Scheduled sends waiting for their undo window. With in-memory job storage the
		// Hangfire job is gone, so this is the only thing that makes a pending send survive
		// a restart.
		var pendingSends = await context
			.OutboxItems.Where(o => o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Pending)
			.Select(o => o.Id)
			.ToListAsync(ct);

		// Left mid-send by a crash, or explicitly ambiguous. Never auto-retried: a send is
		// the one operation whose replay is externally visible.
		var unresolvedSends = await context
			.OutboxItems.Where(o => o.Status == OutboxStatus.Sending || o.Status == OutboxStatus.AmbiguousOutcome)
			.Select(o => o.Id)
			.ToListAsync(ct);

		var exports = await context
			.ExportJobs.Where(job => job.Status == ExportJobStatus.Running || job.Status == ExportJobStatus.CancelRequested)
			.Select(job => job.Id)
			.ToListAsync(ct);

		var notificationAccounts = await context
			.NotificationRecords.Where(record => record.DeliveredAt == null)
			.Select(record => record.AccountId)
			.Distinct()
			.ToListAsync(ct);

		logger.LogInformation(
			"Startup reconciliation found {Backfills} backfills, {Chains} non-terminal mutations, "
				+ "{Ambiguous} unresolved attempts, {Content} unfetched messages, {Drafts} accounts with drafts to push, "
				+ "{CalendarCreations} accounts with calendar creates to reconcile, {Pending} pending sends, "
				+ "{Unresolved} sends awaiting reconciliation, {Exports} incomplete exports, and "
				+ "{Notifications} accounts with undelivered notifications.",
			backfilling.Count,
			chains.Count,
			ambiguous.Count,
			content.Count,
			draftAccounts.Count,
			calendarCreationAccounts.Count,
			pendingSends.Count,
			unresolvedSends.Count,
			exports.Count,
			notificationAccounts.Count
		);

		return new OutstandingWork(
			backfilling,
			chains,
			ambiguous,
			content,
			pendingSends,
			unresolvedSends,
			exports,
			draftAccounts,
			calendarCreationAccounts,
			notificationAccounts
		);
	}

	/// <summary>
	/// Releases leases held by a process that no longer exists.
	/// </summary>
	/// <remarks>
	/// Releasing the lease says only that no worker owns the item. It says nothing about what
	/// the server saw, which is why the item's attempt membership — not its state — decides
	/// whether it may be re-executed or must first be reconciled.
	/// </remarks>
	public async Task<int> ReleaseOrphanedLeasesAsync(CancellationToken ct = default)
	{
		var now = clock.GetUtcNow();
		return await context
			.MutationItems.Where(m => m.State == MutationState.Leased)
			.ExecuteUpdateAsync(
				updates =>
					updates
						.SetProperty(m => m.State, MutationState.Pending)
						.SetProperty(m => m.LeaseOwner, (string?)null)
						.SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)now),
				ct
			);
	}

	/// <summary>
	/// The items that belonged to a dispatched attempt with no persisted result. Ambiguity is
	/// per attempt, not per operation type, so this is queried from attempts — the operation
	/// type determines only <i>how</i> to reconcile.
	/// </summary>
	public async Task<IReadOnlyList<MutationItem>> AmbiguousItemsAsync(CancellationToken ct = default)
	{
		var attemptIds = await context
			.MutationExecutionAttempts.Where(a =>
				(a.State == MutationAttemptState.Dispatched || a.State == MutationAttemptState.Ambiguous)
				&& a.ResultPersistedAt == null
			)
			.Select(a => a.Id)
			.ToListAsync(ct);

		var itemIds = await context
			.MutationExecutionAttemptItems.Where(i => attemptIds.Contains(i.AttemptId))
			.Select(i => i.MutationItemId)
			.ToListAsync(ct);

		return await context.MutationItems.Where(m => itemIds.Contains(m.Id)).ToListAsync(ct);
	}
}
