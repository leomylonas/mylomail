using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Mutations;

/// <summary>
/// The dispatch boundary (§6). Executes one homogeneous batch of claimed mutation items
/// against a provider, and is the only place the <c>Dispatched</c> write happens.
/// </summary>
public sealed class MutationExecutor(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	IProviderMailboxResolver mailboxResolver,
	TimeProvider clock,
	IFaultInjector faults,
	MutationChainEvaluator chains,
	IHubEvents events,
	ILogger<MutationExecutor> logger
)
{
	/// <summary>
	/// Runs the five steps in order. They are numbered in §6 and the order is the mechanism,
	/// not a convention.
	/// </summary>
	/// <param name="items">
	/// Claimed items of one operation kind with one payload. All three providers batch
	/// natively, and multi-select routinely spans hundreds of messages, so a batch is the
	/// default shape rather than an optimisation.
	/// </param>
	public async Task ExecuteAsync(Account account, IReadOnlyList<MutationItem> items, CancellationToken ct = default)
	{
		if (items.Count == 0)
		{
			return;
		}

		var provider = providers.For(account);
		var operation = items[0].OperationKind;


		Guid? resolvedTargetMailboxId = null;
		var cancellations = new List<MutationFailureDto>();
		if (operation == MutationOperationKind.MoveToTrash)
		{
			resolvedTargetMailboxId = mailboxResolver.SpecialMailboxId(account.Id, SpecialUse.Trash);
			if (resolvedTargetMailboxId is null)
			{
				foreach (var item in items)
				{
					cancellations.Add(
						await chains.CancelUnsatisfiableAsync(
							item,
							"This account must have exactly one mailbox configured as Trash.",
							ct
						)
					);
				}
				await AnnounceCancellationsAsync(cancellations, ct);
				return;
			}
		}
		// Execution identity is resolved here, after preceding mutations have settled —
		// never captured at enqueue time, which would preserve the staleness the chain
		// exists to prevent.
		var resolved = new List<(MutationItem Item, MessageOccurrenceRef Ref)>();
		foreach (var item in items)
		{
			if (operation == MutationOperationKind.MoveMessage
				&& (item.TargetMailboxId is null
					|| !await context.Mailboxes.AnyAsync(mailbox => mailbox.Id == item.TargetMailboxId, ct)))
			{
				cancellations.Add(
					await chains.CancelUnsatisfiableAsync(item, "The target mailbox no longer exists.", ct)
				);
				continue;
			}
			var occurrence = await ResolveAsync(item, ct);
			if (occurrence is null)
			{
				// The intent is no longer satisfiable. It is cancelled, never silently
				// broadened to a different occurrence.
				cancellations.Add(
					await chains.CancelUnsatisfiableAsync(
						item,
						"The message is no longer where this operation refers to.",
						ct
					)
				);
				continue;
			}

			resolved.Add((
				item,
				occurrence with { ResolvedTargetMailboxId = resolvedTargetMailboxId }
			));
		}

		await AnnounceCancellationsAsync(cancellations, ct);
		if (resolved.Count == 0)
		{
			return;
		}

		// Step 2 — the attempt and its membership are persisted before anything is sent.
		var attempt = new MutationExecutionAttempt
		{
			Id = Guid.NewGuid(),
			AccountId = account.Id,
			Provider = account.ProviderType,
			OperationKind = operation,
			State = MutationAttemptState.Prepared,
			CreatedAt = clock.GetUtcNow(),
			Items =
			[
				.. resolved.Select(r => new MutationExecutionAttemptItem
				{
					MutationItemId = r.Item.Id,
					ResolvedTargetMailboxId = r.Ref.ResolvedTargetMailboxId,
				}),
			],
		};
		context.MutationExecutionAttempts.Add(attempt);
		await context.SaveChangesAsync(ct);

		faults.Reached(FaultPoints.AfterEnqueueBeforeDispatched);

		// Step 3 — synchronous, durable, immediately before the irreversible call.
		//
		// This is the entire crash-safety mechanism. It must not be batched with step 2,
		// deferred, made asynchronous, or removed: doing any of those silently reopens the
		// window it closes, and the resulting bug is invisible in testing.
		attempt.State = MutationAttemptState.Dispatched;
		attempt.DispatchedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);

		faults.Reached(FaultPoints.AfterDispatchedBeforeProviderCall);

		// Step 4.
		BatchResult result;
		try
		{
			result = await CallAsync(provider, account, operation, items[0], [.. resolved.Select(r => r.Ref)], ct);
		}
		catch (Exception ex) when (ex is not SimulatedCrashException)
		{
			// A thrown provider call is not evidence that nothing happened. The attempt
			// stays ambiguous and the sweep reconciles it; only the local items are failed.
			attempt.State = MutationAttemptState.Ambiguous;
			await context.SaveChangesAsync(ct);

			logger.LogError(
				ex,
				"Mutation attempt {AttemptId} for account {AccountId} failed and is ambiguous.",
				attempt.Id,
				account.Id
			);
			throw;
		}

		faults.Reached(FaultPoints.AfterProviderCallBeforeResults);

		// Step 5 — per-item outcomes and the attempt's terminal state, in one transaction.
		// An attempt is never observable as Completed with unpersisted results, nor results
		// observable without the attempt closed.
		var persisted = await PersistResultsAsync(attempt, resolved, result, ct);

		// After the commit. The user's optimistic change has just been reverted, so this is
		// the only thing standing between them and a flag that silently springs back.
		foreach (var failure in persisted.Failures)
		{
			await events.MessageSyncFailedAsync(failure);
		}

		// A confirmed removal of the last occurrence is the point the message stops existing
		// as far as every open window is concerned (§7).
		var deleted = await MessageChangeAnnouncer.AnnounceDeletedAsync(
			context,
			events,
			persisted.RemovedMessageIds,
			ct
		);

		// A message this batch deleted is not also updated; for the rest, this is the
		// confirmation §7 pairs with mutation job completion.
		await MessageChangeAnnouncer.AnnounceUpdatedAsync(
			context,
			events,
			[
				.. persisted
					.ConfirmedMutations.Where(settlement => !deleted.Contains(settlement.MessageId))
					.Select(settlement => settlement.MessageId),
			],
			ct
		);
		foreach (var settlement in persisted.ConfirmedMutations)
		{
			await events.MessageMutationSettledAsync(settlement);
		}
	}

	private async Task AnnounceCancellationsAsync(
		List<MutationFailureDto> cancellations,
		CancellationToken ct
	)
	{
		if (cancellations.Count == 0)
		{
			return;
		}

		foreach (var cancellation in cancellations)
		{
			await events.MessageSyncFailedAsync(cancellation);
		}

		// The reverted optimistic state is as much of a change to what a window shows as a
		// confirmed one, and only the acting window learns it from its own mutation call.
		await MessageChangeAnnouncer.AnnounceUpdatedAsync(
			context,
			events,
			[.. cancellations.Select(cancellation => cancellation.MessageId)],
			ct
		);
		cancellations.Clear();
	}

	private async Task<PersistedResults> PersistResultsAsync(
		MutationExecutionAttempt attempt,
		List<(MutationItem Item, MessageOccurrenceRef Ref)> resolved,
		BatchResult result,
		CancellationToken ct
	)
	{
		PersistedResults persisted = new([], [], []);
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			var byKey = result.Items.ToDictionary(r => (r.MessageId, r.MailboxId));
			var failed = new List<MutationItem>();
			var unresolved = 0;
			var removed = new List<Guid>();
			var failures = new List<MutationFailureDto>();
			var confirmed = new List<MutationSettledDto>();

			foreach (var (item, reference) in resolved)
			{
				if (!byKey.TryGetValue((reference.MessageId, reference.MailboxId), out var outcome))
				{
					// An item the batch did not report on is unresolved, not successful.
					// Leaving it in the dispatched attempt is what routes it to
					// reconciliation rather than to a blind retry.
					unresolved++;
					continue;
				}

				if (outcome.Succeeded
					&& outcome.OccurrenceChanges.Any(change => change.RequiresDestinationReconciliation))
				{
					// The provider confirmed the move but could not report the destination
					// occurrence identity. Keep the durable attempt open until observation
					// materialises that identity; completing here silently orphans the message.
					unresolved++;
					continue;
				}

				if (outcome.Succeeded)
				{
					await ApplySuccessAsync(item, outcome, removed, ct);
					confirmed.Add(new MutationSettledDto(item.Id, item.MessageId, item.OperationKind));
				}
				else
				{
					item.State = MutationState.Failed;
					item.CompletedAt = clock.GetUtcNow();
					item.LastError = outcome.Problem?.Detail ?? outcome.Problem?.Title;
					item.FailureCategory = outcome.Problem?.Category ?? ErrorCategory.Unknown;
					item.LeaseOwner = null;
					item.LeaseExpiresAt = null;
					await chains.RevertDesiredStateAsync(item, ct);
					failed.Add(item);
					var extensions = outcome.Problem?.Extensions;
					failures.Add(
						new MutationFailureDto(
							item.Id,
							item.MessageId,
							item.AccountId,
							item.OperationKind,
							item.FailureCategory ?? ErrorCategory.Unknown,
							item.LastError,
							extensions?.TryGetValue("hostname", out var hostname) == true ? hostname as string : null,
							extensions?.TryGetValue("sha256Fingerprint", out var fingerprint) == true
								? fingerprint as string
								: null
						)
					);
				}
			}

			if (unresolved > 0)
			{
				// The attempt is not closed, because not every outcome is known. Marking it
				// Completed here would lose the unreported items entirely: the recovery sweep
				// queries from attempts, so an attempt claiming to be finished takes its
				// unresolved members with it and nothing ever reconciles them.
				attempt.State = MutationAttemptState.Ambiguous;

				logger.LogWarning(
					"Attempt {AttemptId} reported on {Reported} of {Submitted} items; the remainder are ambiguous.",
					attempt.Id,
					resolved.Count - unresolved,
					resolved.Count
				);
			}
			else
			{
				attempt.State = MutationAttemptState.Completed;
				attempt.ResultPersistedAt = clock.GetUtcNow();
			}

			await context.SaveChangesAsync(ct);

			// Ordering is not transactionality: later intentions are re-evaluated against
			// current server-known state, not cancelled wholesale.
			foreach (var item in failed)
			{
				failures.AddRange(await chains.ReevaluateAfterFailureAsync(item, ct));
			}

			await context.SaveChangesAsync(ct);
			persisted = new PersistedResults([.. failures], [.. confirmed], [.. removed]);
			await transaction.CommitAsync(ct);
		});

		return persisted;
	}

	private async Task ApplySuccessAsync(
		MutationItem item,
		BatchItemResult outcome,
		List<Guid> removedMessageIds,
		CancellationToken ct
	)
	{
		foreach (var change in outcome.OccurrenceChanges)
		{
			var occurrence = await context.MessageMailboxes.FirstOrDefaultAsync(
				o => o.MessageId == outcome.MessageId && o.MailboxId == change.MailboxId,
				ct
			);

			if (change.Removed)
			{
				if (occurrence is not null)
				{
					context.MessageMailboxes.Remove(occurrence);
					removedMessageIds.Add(outcome.MessageId);
				}

				continue;
			}

			if (change.RequiresDestinationReconciliation)
			{
				// The occurrence exists on the server but is not addressable yet — an IMAP
				// move without UIDPLUS is the normal path here, not an edge case. It is left
				// for reconciliation rather than being written with a guessed id.
				continue;
			}

			if (occurrence is null)
			{
				context.MessageMailboxes.Add(
					new MessageMailbox
					{
						Id = Guid.NewGuid(),
						MessageId = outcome.MessageId,
						MailboxId = change.MailboxId,
						ProviderOccurrenceId = change.NewProviderOccurrenceId!,
					}
				);
			}
			else
			{
				occurrence.ProviderOccurrenceId = change.NewProviderOccurrenceId!;
			}

			// A membership just landed — no longer a GC candidate, however briefly it may
			// have looked like one (§6).
			var landed = await context.Messages.FirstAsync(m => m.Id == outcome.MessageId, ct);
			landed.OrphanedAt = null;
		}

		if (item.OperationKind == MutationOperationKind.SetFlags)
		{
			var message = await context.Messages.FirstOrDefaultAsync(m => m.Id == item.MessageId, ct);
			if (message is not null)
			{
				// Server-known state catches up only once the server has confirmed it.
				message.IsRead = item.DesiredIsRead ?? message.IsRead;
				message.IsFlagged = item.DesiredIsFlagged ?? message.IsFlagged;
			}
		}

		item.State = MutationState.Completed;
		item.CompletedAt = clock.GetUtcNow();
		item.LeaseOwner = null;
		item.LeaseExpiresAt = null;
		await chains.ClearDesiredStateAsync(item, ct);
	}

	/// <summary>
	/// Resolves stable intent to a provider-addressable occurrence, at execution time.
	/// </summary>
	/// <remarks>
	/// <see cref="MutationOperationKind.RemoveFromMailbox"/> is membership-scoped: it
	/// resolves only the membership named in the intent, and returns null if that membership
	/// is gone. Every other operation is message-scoped and resolves wherever the message
	/// actually is now, which is what makes "move to X failed, then move to Y" proceed
	/// correctly.
	/// </remarks>
	private async Task<MessageOccurrenceRef?> ResolveAsync(MutationItem item, CancellationToken ct)
	{
		var query = context.MessageMailboxes.Where(o => o.MessageId == item.MessageId);

		if (item.OperationKind == MutationOperationKind.RemoveFromMailbox)
		{
			query = query.Where(o => o.MailboxId == item.ScopeMailboxId);
		}

		var occurrence = await query.OrderBy(o => o.MailboxId).FirstOrDefaultAsync(ct);

		return occurrence is null
			? null
			: new MessageOccurrenceRef(occurrence.MessageId, occurrence.MailboxId, occurrence.ProviderOccurrenceId);
	}

	private async Task<BatchResult> CallAsync(
		IMailProvider provider,
		Account account,
		MutationOperationKind operation,
		MutationItem exemplar,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		switch (operation)
		{
			case MutationOperationKind.SetFlags:
				return await provider.SetFlagsAsync(
					account,
					refs,
					new FlagUpdate(exemplar.DesiredIsRead, exemplar.DesiredIsFlagged),
					ct
				);

			case MutationOperationKind.MoveMessage:
				{
					var target = await context.Mailboxes.FirstAsync(m => m.Id == exemplar.TargetMailboxId, ct);
					return await provider.MoveMessagesAsync(account, refs, target, ct);
				}

			case MutationOperationKind.RemoveFromMailbox:
				return await provider.RemoveFromMailboxAsync(account, refs, ct);

			case MutationOperationKind.MoveToTrash:
				return await provider.MoveToTrashAsync(account, refs, ct);

			case MutationOperationKind.DeletePermanently:
				return await provider.DeletePermanentlyAsync(account, refs, ct);

			default:
				throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown mutation operation.");
		}
	}
}

internal sealed record PersistedResults(
	IReadOnlyList<MutationFailureDto> Failures,
	IReadOnlyList<MutationSettledDto> ConfirmedMutations,
	IReadOnlyList<Guid> RemovedMessageIds
);
