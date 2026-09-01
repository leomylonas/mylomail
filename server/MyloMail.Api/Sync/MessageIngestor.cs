using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// What one ingest changed, so the caller can announce it.
/// </summary>
/// <remarks>
/// Separated from the ingest itself because <b>only the caller knows which kind of sync this
/// is</b>. A message arriving during backfill is part of a backlog the user already has;
/// the same message arriving from the change stream is new mail. §7 draws that line, and
/// nothing inside the ingestor can see it.
/// </remarks>
/// <param name="Observed">
/// Every message this page's occurrences resolved to, created or not, including a message
/// that already existed with identical content. Notification eligibility uses this rather
/// than <paramref name="Created"/>/<paramref name="Updated"/>: a message backfill already
/// materialised, then reported again by the live stream (or Gmail's replayed staged
/// history), is genuinely new mail even though nothing about its row changed on this pass —
/// exactly the case a row-creation or row-change rule gets wrong (§13 Epic 9).
/// </param>
public sealed record IngestResult(
	IReadOnlyList<Message> Created,
	IReadOnlyList<Message> Updated,
	IReadOnlyList<Message> Observed
);

/// <summary>
/// Turns provider observations into local rows. Every write here is an upsert, which is what
/// makes replaying a page safe — and replay is the price of never skipping one (§3).
/// </summary>
public sealed class MessageIngestor(MyloMailDbContext context)
{
	/// <summary>
	/// Upserts a page of messages into one account, resolving provider mailbox ids to local
	/// mailboxes as it goes.
	/// </summary>
	/// <param name="generations">
	/// The <see cref="Mailbox.TopologyGeneration"/> each mailbox held when the work was
	/// issued, keyed by provider mailbox id. Anything touching a mailbox whose generation has
	/// since moved is discarded: an IMAP folder deleted and recreated with the same name, or a
	/// Graph folder replaced, would otherwise have a late page resurrect or mutate state
	/// belonging to a mailbox that no longer exists (§1).
	/// <para>
	/// It is a map rather than one value because an account-scoped stream reports several
	/// mailboxes in one page, and the generation of the mailbox that happened to be polled
	/// says nothing about the others.
	/// </para>
	/// </param>
	public async Task<IngestResult> IngestAsync(
		Account account,
		IReadOnlyList<MessageDto> messages,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		var created = new List<Message>();
		var updated = new List<Message>();
		var observed = new List<Message>();

		foreach (var dto in messages)
		{
			// A draft on the server is a Draft, never both (§1), so one drafts occurrence
			// excludes the whole observation rather than just that occurrence. Under Gmail's
			// label model a draft carries DRAFT alongside its other labels, and dropping only
			// the DRAFT occurrence would materialise the remaining one as ordinary mail —
			// the same draft appearing twice, which is precisely what the rule forbids.
			//
			// It also has to happen before matching: a drafts observation that found an
			// unrelated message would have its fields overwritten by Apply.
			if (
				dto.Occurrences.Any(occurrence =>
					mailboxesByProviderId.TryGetValue(occurrence.ProviderMailboxId, out var mailbox)
					&& mailbox.SpecialUse == SpecialUse.Drafts
				)
			)
			{
				continue;
			}

			var message = await MatchAsync(account, dto, mailboxesByProviderId, ct);
			var isNew = message is null;
			var membershipChanged = false;

			if (message is null)
			{
				// Where no match can be established a new local message is created:
				// duplicating a message is recoverable, whereas merging two distinct
				// messages is not (§1).
				message = new Message { Id = Guid.NewGuid(), AccountId = account.Id };
				context.Messages.Add(message);

				// Queued as part of the same write that creates the message, so a message can
				// never exist without a content state for the sweep to find (§6).
				context.MessageContentStates.Add(
					new MessageContentState { MessageId = message.Id, Status = ContentStatus.Queued }
				);
			}

			Apply(dto, message);

			foreach (var occurrence in dto.Occurrences)
			{
				if (!mailboxesByProviderId.TryGetValue(occurrence.ProviderMailboxId, out var mailbox))
				{
					// The mailbox is not known locally yet. Topology discovery is a separate
					// concern and runs on its own cadence; the occurrence arrives with the
					// next page after it lands, rather than being invented here.
					continue;
				}

				if (!generations.StillCurrent(occurrence.ProviderMailboxId, mailbox))
				{
					continue;
				}

				membershipChanged |= await UpsertOccurrenceAsync(message, mailbox, occurrence, ct);
			}

			observed.Add(message);

			if (isNew)
			{
				created.Add(message);
			}
			else if (membershipChanged || context.Entry(message).State == EntityState.Modified)
			{
				// Only when something actually differs. A provider that returns the whole
				// mailbox on every poll — IMAP does — would otherwise report every message as
				// updated every minute, and an event that fires when nothing happened tells a
				// listener nothing at all (§7).
				updated.Add(message);
			}
		}

		return new IngestResult(created, updated, observed);
	}

	/// <summary>
	/// The matching precedence from §1, in order: the provider's stable id where it supplies
	/// one, then the per-mailbox occurrence identity, then
	/// <c>(AccountId, MessageIdHeader, ReceivedAt)</c> as a heuristic.
	/// </summary>
	private async Task<Message?> MatchAsync(
		Account account,
		MessageDto dto,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		CancellationToken ct
	)
	{
		if (dto.ProviderStableId is not null)
		{
			var byStableId = await context.Messages.FirstOrDefaultAsync(
				m => m.AccountId == account.Id && m.ProviderStableId == dto.ProviderStableId,
				ct
			);

			if (byStableId is not null)
			{
				return byStableId;
			}
		}

		foreach (var occurrence in dto.Occurrences)
		{
			// Scoped to the mailbox, because an occurrence id is only unique within one.
			// An IMAP UID is per-folder, so an unscoped lookup merges the message holding
			// UID 2 in the Drafts folder with the unrelated one holding UID 2 in the inbox —
			// and §1 is explicit that merging two distinct messages is the unrecoverable
			// direction. Observed: a leftover draft rewrote a seeded inbox message's subject.
			if (!mailboxesByProviderId.TryGetValue(occurrence.ProviderMailboxId, out var mailbox))
			{
				continue;
			}

			var byOccurrence = await context
				.MessageMailboxes.Where(o =>
					o.MailboxId == mailbox.Id
					&& o.ProviderOccurrenceId == occurrence.ProviderOccurrenceId
				)
				.Join(
					context.Messages.Where(m => m.AccountId == account.Id),
					o => o.MessageId,
					m => m.Id,
					(_, m) => m
				)
				.FirstOrDefaultAsync(ct);

			if (byOccurrence is not null)
			{
				return byOccurrence;
			}
		}

		// A Message-ID is metadata, not identity — RFC 5322 says SHOULD, not MUST, and
		// duplicates occur in practice — so it is paired with the received time and used only
		// as a last resort.
		if (dto.MessageIdHeader is not null)
		{
			return await context.Messages.FirstOrDefaultAsync(
				m =>
					m.AccountId == account.Id
					&& m.MessageIdHeader == dto.MessageIdHeader
					&& m.ReceivedAt == dto.ReceivedAt,
				ct
			);
		}

		return null;
	}

	/// <summary>
	/// Writes server-known state. Locally desired changes live in
	/// <see cref="MessagePendingChange"/> and are deliberately untouched here: an incoming
	/// observation is what the server believes, and merging it must not discard what the user
	/// asked for and has not yet had confirmed (§6).
	/// </summary>
	private static void Apply(MessageDto dto, Message message)
	{
		message.ProviderStableId = dto.ProviderStableId ?? message.ProviderStableId;
		message.MessageIdHeader = dto.MessageIdHeader;
		message.InReplyToHeader = dto.InReplyToHeader;
		message.ReferencesHeader = dto.ReferencesHeader;
		message.ReplyToAddresses = dto.ReplyToAddresses;
		message.SenderAddress = dto.SenderAddress;
		message.ThreadId = dto.ThreadId;
		message.From = dto.From;
		message.To = dto.To;
		message.Cc = dto.Cc;
		message.Bcc = dto.Bcc;
		message.Subject = dto.Subject;
		message.Snippet = dto.Snippet;
		message.ReceivedAt = dto.ReceivedAt;
		message.IsRead = dto.IsRead;
		message.IsFlagged = dto.IsFlagged;
		message.IsDraft = dto.IsDraft;
		message.IsAnswered = dto.IsAnswered;
		message.HasNonInlineAttachments = dto.HasNonInlineAttachments;
		message.SizeEstimate = dto.SizeEstimate;
	}

	/// <summary>Upserts one membership, reporting whether it actually changed anything.</summary>
	private async Task<bool> UpsertOccurrenceAsync(
		Message message,
		Mailbox mailbox,
		MessageOccurrenceDto dto,
		CancellationToken ct
	)
	{
		var existing =
			context
				.ChangeTracker.Entries<MessageMailbox>()
				.Select(e => e.Entity)
				.FirstOrDefault(o => o.MessageId == message.Id && o.MailboxId == mailbox.Id)
			?? await context.MessageMailboxes.FirstOrDefaultAsync(
				o => o.MessageId == message.Id && o.MailboxId == mailbox.Id,
				ct
			);

		if (existing is null)
		{
			context.MessageMailboxes.Add(
				new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = message.Id,
					MailboxId = mailbox.Id,
					ProviderOccurrenceId = dto.ProviderOccurrenceId,
					ImapModSeq = dto.ImapModSeq,
				}
			);
			return true;
		}

		existing.ProviderOccurrenceId = dto.ProviderOccurrenceId;
		existing.ImapModSeq = dto.ImapModSeq ?? existing.ImapModSeq;
		return context.Entry(existing).State == EntityState.Modified;
	}

	/// <summary>
	/// Removes one membership. Never the canonical message: under Graph's folder-scoped delta
	/// a move surfaces as a removal and an addition in either order, so a canonical message
	/// may transiently have zero memberships (§3).
	/// </summary>
	public async Task RemoveOccurrenceAsync(
		Mailbox mailbox,
		string providerOccurrenceId,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		// A removal issued against a previous incarnation of this mailbox would delete a real
		// occurrence belonging to the new one — silently, since the provider occurrence id can
		// legitimately repeat across incarnations.
		if (!generations.StillCurrent(mailbox.ProviderMailboxId, mailbox))
		{
			return;
		}

		var occurrence = await context.MessageMailboxes.FirstOrDefaultAsync(
			o => o.MailboxId == mailbox.Id && o.ProviderOccurrenceId == providerOccurrenceId,
			ct
		);

		if (occurrence is not null)
		{
			context.MessageMailboxes.Remove(occurrence);
		}
	}

	/// <summary>Applies a server-observed flag change to server-known state.</summary>
	public async Task<Message?> ApplyFlagChangeAsync(
		Mailbox mailbox,
		OccurrenceFlagChange change,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		if (!generations.StillCurrent(mailbox.ProviderMailboxId, mailbox))
		{
			return null;
		}

		var occurrence = await context.MessageMailboxes.FirstOrDefaultAsync(
			o => o.MailboxId == mailbox.Id && o.ProviderOccurrenceId == change.ProviderOccurrenceId,
			ct
		);

		if (occurrence is null)
		{
			return null;
		}

		var message = await context.Messages.FirstOrDefaultAsync(m => m.Id == occurrence.MessageId, ct);
		if (message is null)
		{
			return null;
		}

		message.IsRead = change.IsRead ?? message.IsRead;
		message.IsFlagged = change.IsFlagged ?? message.IsFlagged;
		occurrence.ImapModSeq = change.ImapModSeq ?? occurrence.ImapModSeq;
		return message;
	}
}
