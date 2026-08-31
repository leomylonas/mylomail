using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Outbox;

/// <summary>
/// Sends one outbox item across the dispatch boundary (§6, §15).
/// </summary>
/// <remarks>
/// Send follows the same five steps as any other mutation and differs in one respect that
/// changes everything downstream: <b>its ambiguous outcome is externally visible.</b> A
/// replayed flag set converges; a replayed send puts a second copy of a message in someone
/// else's inbox. So the recovery policy is <see cref="MutationRecoveryPolicy.AmbiguousOutcome"/>
/// and nothing here ever retries on its own.
/// </remarks>
public sealed class SendExecutor(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	OutboxService outbox,
	TimeProvider clock,
	IFaultInjector faults,
	ILogger<SendExecutor> logger
)
{
	public async Task SendAsync(Account account, Guid outboxItemId, CancellationToken ct = default)
	{
		// The claim is the compare-and-swap that races cancellation. Losing it is a normal
		// outcome, not an error: the user cancelled in time.
		if (!await outbox.TryClaimForSendAsync(outboxItemId, ct))
		{
			logger.LogInformation("Outbox item {OutboxItemId} was not claimable; cancellation won.", outboxItemId);
			return;
		}

		var item = await context.OutboxItems.FirstAsync(o => o.Id == outboxItemId, ct);
		var draft = await context.Drafts.FirstOrDefaultAsync(d => d.Id == item.DraftId, ct);

		if (draft is null)
		{
			item.Status = OutboxStatus.Failed;
			item.LastError = "The draft this send refers to no longer exists.";
			await context.SaveChangesAsync(ct);
			return;
		}

		// Step 2 — the attempt, with exactly one item by construction.
		var attempt = new MutationExecutionAttempt
		{
			Id = Guid.NewGuid(),
			AccountId = account.Id,
			Provider = account.ProviderType,
			OperationKind = MutationOperationKind.Send,
			State = MutationAttemptState.Prepared,
			CreatedAt = clock.GetUtcNow(),
			OutboxItemId = item.Id,
		};
		context.MutationExecutionAttempts.Add(attempt);
		item.Attempts++;
		await context.SaveChangesAsync(ct);

		faults.Reached(FaultPoints.AfterEnqueueBeforeDispatched);

		// Step 3 — synchronous and durable, immediately before the irreversible call. For
		// send this is the difference between "we may have sent it" and losing the fact
		// entirely.
		attempt.State = MutationAttemptState.Dispatched;
		attempt.DispatchedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);

		faults.Reached(FaultPoints.AfterDispatchedBeforeProviderCall);

		try
		{
			await providers.For(account).SendAsync(account, draft, item.StableMessageId, ct);
		}
		catch (Exception ex) when (ex is ProviderThrottledException or ProviderAuthenticationException)
		{
			// An explicit categorised rejection is an *observed* outcome, not an absent one:
			// the provider answered, and the answer was no. The message was not sent, so the
			// item returns to the queue rather than becoming ambiguous — otherwise every
			// throttled send would leave the user with a message they must go and check the
			// Sent mailbox for, and undo-send would degrade badly under rate limiting.
			item.Status = OutboxStatus.Scheduled;
			item.LastError = ex.Message;

			attempt.State = MutationAttemptState.Completed;
			attempt.ResultPersistedAt = clock.GetUtcNow();
			await context.SaveChangesAsync(ct);

			// Rethrown so the job layer applies the account gate and reschedules at exactly
			// the delay the provider named.
			throw;
		}
		catch (Exception ex)
		{
			// A thrown send is not evidence that nothing was sent. The item becomes
			// ambiguous rather than failed, and reconciliation — not a retry — decides.
			attempt.State = MutationAttemptState.Ambiguous;
			item.Status = OutboxStatus.AmbiguousOutcome;
			item.LastError = ex.Message;
			item.ReconcilingSince = clock.GetUtcNow();
			await context.SaveChangesAsync(ct);

			logger.LogError(ex, "Send for outbox item {OutboxItemId} may or may not have happened.", item.Id);
			return;
		}

		faults.Reached(FaultPoints.AfterProviderCallBeforeResults);

		// Step 5 — the outcome and the attempt's terminal state in one transaction.
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			item.Status = OutboxStatus.Sent;
			item.SentAt = clock.GetUtcNow();
			item.LastError = null;

			attempt.State = MutationAttemptState.Completed;
			attempt.ResultPersistedAt = clock.GetUtcNow();

			// The draft has become a sent message and is no longer an authoring document.
			context.Drafts.Remove(draft);

			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});
	}
}
