using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Imap;

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
			// The CAS claim above already announced Scheduled -> Sending (§7); this write must
			// announce the terminal state too, or the renderer is left showing a stuck "Sending"
			// item with no explanation.
			await outbox.AnnounceStatusAsync(item.Id, ct);
			return;
		}

		// The item may have been queued before a concurrent sync flipped SyncConflict on its
		// draft (§1, §15) — DraftService.SendAsync only guards queueing itself, not an item
		// already sitting in the queue. Sending now would build MIME straight from the local
		// copy and silently discard whatever the server's copy actually holds, the same
		// overwrite the conflict flag exists to prevent. Failing here, the same way a
		// vanished draft already does, routes the user back through
		// DraftService.ResolveConflictAsync instead.
		if (draft.SyncConflict)
		{
			item.Status = OutboxStatus.Failed;
			item.LastError = "This draft has an unresolved sync conflict. Resolve it, then send again.";
			await context.SaveChangesAsync(ct);
			await outbox.AnnounceStatusAsync(item.Id, ct);
			return;
		}

		// Resolved here rather than stored on the draft: the identity is the stored fact and
		// its address is derived from it, so there is one answer to which address a draft
		// sends from (§1).
		draft.FromAddress = await context
			.SendIdentities.Where(i => i.Id == draft.SendIdentityId)
			.Select(i => i.EmailAddress)
			.FirstAsync(ct);

		if (draft.InReplyToMessageId is Guid inReplyTo)
		{
			var parent = await context
				.Messages.Where(m => m.Id == inReplyTo)
				.Select(m => new { m.MessageIdHeader, m.ReferencesHeader })
				.FirstOrDefaultAsync(ct);
			draft.InReplyToHeader = parent?.MessageIdHeader;
			// RFC 5322 §3.6.4: References is the parent's own References chain with the
			// parent's Message-ID appended — not just the immediate parent's id — so a client
			// that threads solely on References (rather than In-Reply-To) can still reconstruct
			// a thread more than one reply deep. IMAP's ENVELOPE fetch never carries References
			// at all (RFC 3501 does not include it), so this is empty more often for a reply to
			// an IMAP-sourced message than a Gmail/Graph one — a real, accepted transport
			// limitation, not something this fix can close.
			if (parent?.MessageIdHeader is string parentMessageId)
			{
				draft.ReferencesHeader = string.IsNullOrWhiteSpace(parent.ReferencesHeader)
					? parentMessageId
					: $"{parent.ReferencesHeader} {parentMessageId}";
			}
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

		IMailProvider provider;
		try
		{
			provider = providers.For(account);
			await provider.SendAsync(account, draft, item.StableMessageId, ct);

			// IMAP-only, and deliberately not a send failure of its own (see the doc comment
			// on ImapMailProvider.SendAsync): the message already left, so a failed Sent-copy
			// append cannot retry without risking a duplicate. Until now nothing read this
			// property at all, so a filing failure was invisible everywhere — not even logged
			// — leaving no trail for "why is this sent message missing from Sent" support.
			if (AppendFailureOf(provider) is string appendFailure)
			{
				logger.LogWarning(
					"Outbox item {OutboxItemId} sent successfully but its Sent-folder copy could not be filed: {AppendFailure}",
					item.Id,
					appendFailure
				);
			}
		}
		catch (Exception ex)
			when (ex is ProviderThrottledException or ProviderAuthenticationException or CredentialStoreUnavailableException)
		{
			// An explicit categorised rejection is an *observed* outcome, not an absent one:
			// the provider answered, and the answer was no. The message was not sent, so the
			// item returns to the queue rather than becoming ambiguous — otherwise every
			// throttled send would leave the user with a message they must go and check the
			// Sent mailbox for, and undo-send would degrade badly under rate limiting.
			// CredentialStoreUnavailableException belongs here too even though it isn't a
			// provider rejection: it fails before providers.For(account) can even build a
			// client to dial out with, so nothing was dispatched either.
			item.Status = OutboxStatus.Scheduled;
			item.LastError = ex.Message;

			attempt.State = MutationAttemptState.Completed;
			attempt.ResultPersistedAt = clock.GetUtcNow();
			await context.SaveChangesAsync(ct);
			await outbox.AnnounceStatusAsync(item.Id, ct);

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
			await outbox.AnnounceStatusAsync(item.Id, ct);

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

		await outbox.AnnounceStatusAsync(item.Id, ct);
	}

	/// <summary>Only <see cref="ImapMailProvider"/> can ever report an append failure — Gmail
	/// and Graph never take this shape, and pattern-matching on the concrete type is safe since
	/// every provider is an independent, unrelated implementation of <see cref="IMailProvider"/>,
	/// not a shared base class something else could accidentally also match.</summary>
	internal static string? AppendFailureOf(IMailProvider provider) =>
		provider is ImapMailProvider { AppendFailure: string appendFailure } ? appendFailure : null;
}
