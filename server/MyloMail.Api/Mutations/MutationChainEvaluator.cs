using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Mutations;

/// <summary>
/// What happens to the rest of a chain after one of its items fails, and the optimistic
/// state that goes with it (§6).
/// </summary>
public sealed class MutationChainEvaluator(MyloMailDbContext context, TimeProvider clock, IHubEvents events)
{
	/// <summary>
	/// Re-evaluates later intentions against current server-known state.
	/// </summary>
	/// <remarks>
	/// <b>A chain establishes ordering, not a transaction.</b> The provider offers no
	/// transactional semantics across these operations, so cancelling the whole chain would
	/// turn one provider failure into several abandoned user intentions. Intentions that
	/// remain independently satisfiable continue; only those whose prerequisites are no
	/// longer satisfiable are cancelled, carrying the originating failure.
	/// </remarks>
	public async Task ReevaluateAfterFailureAsync(MutationItem failed, CancellationToken ct = default)
	{
		var later = await context
			.MutationItems.Where(m =>
				m.AccountId == failed.AccountId
				&& m.MessageId == failed.MessageId
				&& m.Sequence > failed.Sequence
				&& m.State != MutationState.Completed
				&& m.State != MutationState.Failed
				&& m.State != MutationState.Cancelled
			)
			.OrderBy(m => m.Sequence)
			.ToListAsync(ct);

		foreach (var item in later)
		{
			if (await IsStillSatisfiableAsync(item, ct))
			{
				continue;
			}

			item.State = MutationState.Cancelled;
			item.CompletedAt = clock.GetUtcNow();
			item.FailureCategory = failed.FailureCategory ?? ErrorCategory.Unknown;

			// Carries the originating failure, so the user is told why an intention they
			// expressed will not happen rather than watching it disappear.
			item.LastError = failed.LastError;
			item.LeaseOwner = null;
			item.LeaseExpiresAt = null;

			await RevertDesiredStateAsync(item, ct);

			// The user asked for this and it will not happen. Saying so is the whole reason
			// the failure carries the originating cause (§6).
			await events.MessageSyncFailedAsync(
				new MutationFailureDto(
					item.MessageId,
					item.FailureCategory ?? ErrorCategory.Unknown,
					item.LastError
				)
			);
		}
	}

	/// <summary>
	/// Satisfiability falls out of intent semantics and execution-time resolution, without
	/// explicit dependency metadata: a membership-scoped removal needs that membership to
	/// exist, an explicit causal dependency needs its predecessor to have succeeded, and
	/// everything else resolves wherever the message actually is.
	/// </summary>
	private async Task<bool> IsStillSatisfiableAsync(MutationItem item, CancellationToken ct)
	{
		if (item.DependsOnMutationItemId is Guid dependency)
		{
			var predecessor = await context.MutationItems.FirstOrDefaultAsync(m => m.Id == dependency, ct);
			if (predecessor is null || predecessor.State != MutationState.Completed)
			{
				return false;
			}
		}

		if (item.OperationKind == MutationOperationKind.RemoveFromMailbox)
		{
			return await context.MessageMailboxes.AnyAsync(
				o => o.MessageId == item.MessageId && o.MailboxId == item.ScopeMailboxId,
				ct
			);
		}

		return await context.MessageMailboxes.AnyAsync(o => o.MessageId == item.MessageId, ct);
	}

	/// <summary>Cancels an intent whose target no longer exists, and reverts its desired state.</summary>
	public async Task CancelUnsatisfiableAsync(MutationItem item, string reason, CancellationToken ct = default)
	{
		item.State = MutationState.Cancelled;
		item.CompletedAt = clock.GetUtcNow();
		item.LastError = reason;
		item.FailureCategory = ErrorCategory.Conflict;
		item.LeaseOwner = null;
		item.LeaseExpiresAt = null;

		await RevertDesiredStateAsync(item, ct);
		await context.SaveChangesAsync(ct);
	}

	/// <summary>
	/// Terminal failure reverts desired state deterministically — by removing this item's own
	/// pending changes only, so a concurrent pending operation on another field survives.
	/// </summary>
	public Task RevertDesiredStateAsync(MutationItem item, CancellationToken ct = default) =>
		RemovePendingChangesAsync(item, ct);

	/// <summary>Success promotes desired state into server-known state, so the pending record is no longer needed.</summary>
	public Task ClearDesiredStateAsync(MutationItem item, CancellationToken ct = default) =>
		RemovePendingChangesAsync(item, ct);

	private async Task RemovePendingChangesAsync(MutationItem item, CancellationToken ct)
	{
		var pending = await context
			.MessagePendingChanges.Where(p => p.MutationItemId == item.Id)
			.ToListAsync(ct);

		context.MessagePendingChanges.RemoveRange(pending);
	}
}
