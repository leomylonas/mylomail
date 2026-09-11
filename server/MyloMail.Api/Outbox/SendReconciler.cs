using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contacts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Outbox;

/// <summary>
/// Resolves sends whose outcome was never observed (§6, §15).
/// </summary>
/// <remarks>
/// <para>
/// A send left <see cref="OutboxStatus.Sending"/> by a crash, or explicitly marked
/// <see cref="OutboxStatus.AmbiguousOutcome"/>, is <b>never auto-retried</b>. It is resolved
/// by looking for the message in the Sent mailbox by the stable <c>Message-ID</c> generated
/// before the first attempt — the one identifier that exists on both sides of an unobserved
/// send.
/// </para>
/// <para>
/// The search is bounded in time rather than immediate. Neither Gmail nor Graph guarantees
/// the Sent copy is available the moment the call returns, so declaring a send failed because
/// the copy has not appeared yet would produce exactly the duplicate this exists to prevent.
/// </para>
/// </remarks>
public sealed class SendReconciler(
	MyloMailDbContext context,
	TimeProvider clock,
	ContactSuggestionService contactSuggestions,
	OutboxService outbox,
	IHubEvents events,
	ILogger<SendReconciler> logger
)
{
	/// <summary>
	/// How long to keep looking before giving up and asking the user.
	/// </summary>
	/// <remarks>
	/// Expiry does not mean "not sent" and must never trigger a resend. It means nobody can
	/// tell, which is a question only the user can answer by looking at their Sent mail.
	/// </remarks>
	public static readonly TimeSpan ReconciliationWindow = TimeSpan.FromMinutes(10);

	public async Task<int> ReconcileAsync(Guid accountId, CancellationToken ct = default)
	{
		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		var unresolved = await context
			.OutboxItems.Where(o =>
				o.AccountId == accountId
				&& (o.Status == OutboxStatus.Sending || o.Status == OutboxStatus.AmbiguousOutcome)
			)
			.ToListAsync(ct);
		var draftIds = unresolved.Select(item => item.DraftId).Distinct().ToArray();
		var draftsById = await context.Drafts
			.Where(draft => draftIds.Contains(draft.Id))
			.ToDictionaryAsync(draft => draft.Id, ct);

		var resolved = 0;
		// Announced only after SaveChangesAsync commits, below: AnnounceStatusAsync reads back
		// through an AsNoTracking query (§7), which would not see this loop's own uncommitted
		// writes yet.
		var toAnnounce = new List<Guid>();
		var contactSuggestionsChanged = false;

		foreach (var item in unresolved)
		{
			// A crash leaves the item in Sending. That is an unobserved outcome, not a
			// failure, so it enters the same reconciliation as an explicit ambiguity.
			if (item.Status == OutboxStatus.Sending)
			{
				item.Status = OutboxStatus.AmbiguousOutcome;
				item.ReconcilingSince ??= clock.GetUtcNow();
			}

			item.ReconcilingSince ??= clock.GetUtcNow();

			if (await FoundInSentAsync(accountId, item.StableMessageId, ct))
			{
				item.Status = OutboxStatus.Sent;
				item.SentAt = clock.GetUtcNow();
				item.LastError = null;
				resolved++;
				contactSuggestionsChanged |= await contactSuggestions.ObserveAsync(
					accountId,
					item.RecipientSnapshot,
					ct
				);
				if (draftsById.Remove(item.DraftId, out var draft))
					context.Drafts.Remove(draft);

				await CloseAttemptAsync(item.Id, ct);
				toAnnounce.Add(item.Id);
				continue;
			}

			if (clock.GetUtcNow() - item.ReconcilingSince >= ReconciliationWindow)
			{
				// Still unknown. It is left for the user rather than resent: a duplicate is
				// not recoverable, and only they can see whether it arrived.
				item.LastError =
					"This message was sent but its outcome could not be confirmed. "
					+ "Check your Sent mail before sending it again.";
				toAnnounce.Add(item.Id);

				logger.LogWarning(
					"Outbox item {OutboxItemId} remains unresolved after {Window}.",
					item.Id,
					ReconciliationWindow
				);
			}
		}

		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		if (contactSuggestionsChanged)
			await events.ContactsChangedAsync(accountId);

		// A resolved Sent, or an expiry that finally set LastError, both change what an
		// already-open compose window shows (§7) — without this it stays on "Confirming this
		// was sent…" even once the real answer is known, same bug shape as pass 58/59's
		// missing-announcement fixes.
		foreach (var itemId in toAnnounce)
		{
			await outbox.AnnounceStatusAsync(itemId, ct);
		}

		return resolved;
	}

	/// <summary>
	/// Whether the message has appeared in the account's Sent mailbox.
	/// </summary>
	/// <remarks>
	/// This reads the local store rather than querying the provider directly: sync already
	/// materialises the Sent mailbox, and a second, separate lookup path would be a second
	/// answer to the same question. The bounded window is what makes waiting for sync
	/// acceptable.
	/// </remarks>
	private async Task<bool> FoundInSentAsync(Guid accountId, string stableMessageId, CancellationToken ct) =>
		await context
			.Messages.Where(m => m.AccountId == accountId && m.MessageIdHeader == stableMessageId)
			.Join(context.MessageMailboxes, m => m.Id, o => o.MessageId, (m, o) => o.MailboxId)
			.Join(
				// (SpecialUseOverride ?? SpecialUse), spelled out rather than via the
				// EffectiveSpecialUse property: EF Core translates this simple property
				// access to SQL COALESCE, but would not translate a C# computed property.
				context.Mailboxes.Where(mb => (mb.SpecialUseOverride ?? mb.SpecialUse) == SpecialUse.Sent),
				mailboxId => mailboxId,
				mailbox => mailbox.Id,
				(_, mailbox) => mailbox.Id
			)
			.AnyAsync(ct);

	private async Task CloseAttemptAsync(Guid outboxItemId, CancellationToken ct)
	{
		// Ordered client-side: SQLite cannot ORDER BY a DateTimeOffset, and an outbox item has
		// at most a handful of attempts.
		var attempts = await context
			.MutationExecutionAttempts.Where(a => a.OutboxItemId == outboxItemId)
			.ToListAsync(ct);
		var attempt = attempts.MaxBy(a => a.CreatedAt);

		if (attempt is not null)
		{
			attempt.State = MutationAttemptState.Completed;
			attempt.ResultPersistedAt = clock.GetUtcNow();
		}
	}
}
