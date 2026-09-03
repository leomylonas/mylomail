using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Enqueues user intentions and the optimistic local state that goes with them (§6).
/// </summary>
public sealed class MutationQueue(
	MyloMailDbContext context,
	TimeProvider clock,
	IFaultInjector faults,
	IMutationDispatcher dispatcher
)
{
	public Task<MutationItem> SetFlagsAsync(
		Guid accountId,
		Guid messageId,
		FlagUpdate update,
		CancellationToken ct = default
	) =>
		EnqueueAsync(
			new MutationItem
			{
				AccountId = accountId,
				MessageId = messageId,
				OperationKind = MutationOperationKind.SetFlags,
				DesiredIsRead = update.IsRead,
				DesiredIsFlagged = update.IsFlagged,
			},
			ct
		);

	public Task<MutationItem> MoveAsync(
		Guid accountId,
		Guid messageId,
		Guid targetMailboxId,
		CancellationToken ct = default
	) =>
		EnqueueAsync(
			new MutationItem
			{
				AccountId = accountId,
				MessageId = messageId,
				OperationKind = MutationOperationKind.MoveMessage,
				TargetMailboxId = targetMailboxId,
			},
			ct
		);

	public Task<MutationItem> RemoveFromMailboxAsync(
		Guid accountId,
		Guid messageId,
		Guid mailboxId,
		CancellationToken ct = default
	) =>
		EnqueueAsync(
			new MutationItem
			{
				AccountId = accountId,
				MessageId = messageId,
				OperationKind = MutationOperationKind.RemoveFromMailbox,
				ScopeMailboxId = mailboxId,
			},
			ct
		);

	public Task<MutationItem> MoveToTrashAsync(Guid accountId, Guid messageId, CancellationToken ct = default) =>
		EnqueueAsync(
			new MutationItem
			{
				AccountId = accountId,
				MessageId = messageId,
				OperationKind = MutationOperationKind.MoveToTrash,
			},
			ct
		);

	public Task<MutationItem> DeletePermanentlyAsync(
		Guid accountId,
		Guid messageId,
		CancellationToken ct = default
	) =>
		EnqueueAsync(
			new MutationItem
			{
				AccountId = accountId,
				MessageId = messageId,
				OperationKind = MutationOperationKind.DeletePermanently,
			},
			ct
		);

	/// <summary>
	/// Assigns the sequence and inserts in one transaction, together with the optimistic
	/// desired state.
	/// </summary>
	/// <remarks>
	/// The sequence is read and written inside the transaction, and a unique index on
	/// <c>(AccountId, MessageId, Sequence)</c> backs it: a concurrent enqueue for the same
	/// message fails the insert rather than silently producing two items that both believe
	/// they are next, which is the failure a read-then-write without the index would produce
	/// under exactly the multi-select the UI makes routine.
	/// </remarks>
	public async Task<MutationItem> EnqueueAsync(MutationItem item, CancellationToken ct = default)
	{
		// A client-supplied messageId is never trusted against the accountId it arrived
		// alongside — the same gap pass 71 closed for a mailbox's parent id. Without this, a
		// mismatched pair would enqueue a MutationItem tagged for the wrong account, and
		// MutationExecutor resolves the message's provider occurrence by MessageId alone (no
		// AccountId filter), so it would go on to authenticate as one account while acting on
		// another's message.
		var actualAccountId = await context
			.Messages.Where(m => m.Id == item.MessageId)
			.Select(m => (Guid?)m.AccountId)
			.FirstOrDefaultAsync(ct);
		if (actualAccountId is Guid found && found != item.AccountId)
		{
			throw new HubException($"Message {item.MessageId} does not belong to account {item.AccountId}.");
		}

		var strategy = context.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			var highest = await context
				.MutationItems.Where(m => m.AccountId == item.AccountId && m.MessageId == item.MessageId)
				.MaxAsync(m => (long?)m.Sequence, ct);

			item.Id = item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
			item.Sequence = (highest ?? 0) + 1;
			item.State = MutationState.Pending;
			item.CreatedAt = clock.GetUtcNow();

			context.MutationItems.Add(item);
			await UpsertPendingChangesAsync(item, ct);

			await context.SaveChangesAsync(ct);

			// The optimistic state and the mutation that owns it are committed together, so
			// desired state can never be orphaned by a crash between the two.
			faults.Reached(FaultPoints.AfterOptimisticCommit);

			await transaction.CommitAsync(ct);

			// Only once the intent is durable. Asking for execution before the commit would
			// race a worker against a transaction that might still roll back, and the user's
			// change would appear to happen and then un-happen.
			dispatcher.RequestDrain(item.AccountId);

			return item;
		});
	}

	/// <summary>
	/// Desired state is per message per field. A single pending boolean could not represent
	/// three concurrent operations, and the first to complete would clear it while two
	/// remained outstanding.
	/// </summary>
	/// <remarks>
	/// Marking a message read and then unread while the first is still in flight is ordinary
	/// use, so the newest intent takes ownership of the field rather than colliding with the
	/// older one. Execution order is still governed by the chain; only the value the UI shows
	/// meanwhile is replaced. The older item's revert then correctly removes nothing, because
	/// the row it owned is no longer its own.
	/// </remarks>
	private async Task UpsertPendingChangesAsync(MutationItem item, CancellationToken ct)
	{
		if (item.OperationKind != MutationOperationKind.SetFlags)
		{
			return;
		}

		if (item.DesiredIsRead is bool read)
		{
			await UpsertAsync(item, MessageFlagField.IsRead, read, ct);
		}

		if (item.DesiredIsFlagged is bool flagged)
		{
			await UpsertAsync(item, MessageFlagField.IsFlagged, flagged, ct);
		}
	}

	private async Task UpsertAsync(MutationItem item, MessageFlagField field, bool desired, CancellationToken ct)
	{
		var existing = await context.MessagePendingChanges.FirstOrDefaultAsync(
			p => p.MessageId == item.MessageId && p.Field == field,
			ct
		);

		if (existing is null)
		{
			context.MessagePendingChanges.Add(
				new MessagePendingChange
				{
					Id = Guid.NewGuid(),
					MessageId = item.MessageId,
					Field = field,
					DesiredValue = desired,
					MutationItemId = item.Id,
				}
			);
			return;
		}

		existing.DesiredValue = desired;
		existing.MutationItemId = item.Id;
	}
}
