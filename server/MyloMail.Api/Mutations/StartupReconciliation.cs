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
	IReadOnlyList<Guid> UnfetchedContentMessages
);

public sealed class StartupReconciliation(MyloMailDbContext context, TimeProvider clock, ILogger<StartupReconciliation> logger)
{
	/// <summary>
	/// Enumerates every source of outstanding work this build has tables for.
	/// </summary>
	/// <remarks>
	/// Outbox, export and notification sources from §6's table are absent here because those
	/// tables do not exist yet. They are listed in the handoff rather than silently omitted:
	/// an under-enumerated sweep is exactly the failure this class exists to prevent.
	/// </remarks>
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

		logger.LogInformation(
			"Startup reconciliation found {Backfills} backfills, {Chains} non-terminal mutations, "
				+ "{Ambiguous} unresolved attempts, {Content} unfetched messages.",
			backfilling.Count,
			chains.Count,
			ambiguous.Count,
			content.Count
		);

		return new OutstandingWork(backfilling, chains, ambiguous, content);
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
