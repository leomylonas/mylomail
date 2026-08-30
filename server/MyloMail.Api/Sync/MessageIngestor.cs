using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

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
	public async Task<int> IngestAsync(
		Account account,
		IReadOnlyList<MessageDto> messages,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		var ingested = 0;

		foreach (var dto in messages)
		{
			var message = await MatchAsync(account, dto, ct);

			if (message is null)
			{
				// Where no match can be established a new local message is created:
				// duplicating a message is recoverable, whereas merging two distinct
				// messages is not (§1).
				message = new Message { Id = Guid.NewGuid(), AccountId = account.Id };
				context.Messages.Add(message);
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

				await UpsertOccurrenceAsync(message, mailbox, occurrence, ct);
			}

			ingested++;
		}

		return ingested;
	}

	/// <summary>
	/// The matching precedence from §1, in order: the provider's stable id where it supplies
	/// one, then the per-mailbox occurrence identity, then
	/// <c>(AccountId, MessageIdHeader, ReceivedAt)</c> as a heuristic.
	/// </summary>
	private async Task<Message?> MatchAsync(Account account, MessageDto dto, CancellationToken ct)
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
			var byOccurrence = await context
				.MessageMailboxes.Where(o => o.ProviderOccurrenceId == occurrence.ProviderOccurrenceId)
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

	private async Task UpsertOccurrenceAsync(
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
			return;
		}

		existing.ProviderOccurrenceId = dto.ProviderOccurrenceId;
		existing.ImapModSeq = dto.ImapModSeq ?? existing.ImapModSeq;
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
	public async Task ApplyFlagChangeAsync(
		Mailbox mailbox,
		OccurrenceFlagChange change,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		if (!generations.StillCurrent(mailbox.ProviderMailboxId, mailbox))
		{
			return;
		}

		var occurrence = await context.MessageMailboxes.FirstOrDefaultAsync(
			o => o.MailboxId == mailbox.Id && o.ProviderOccurrenceId == change.ProviderOccurrenceId,
			ct
		);

		if (occurrence is null)
		{
			return;
		}

		var message = await context.Messages.FirstOrDefaultAsync(m => m.Id == occurrence.MessageId, ct);
		if (message is null)
		{
			return;
		}

		message.IsRead = change.IsRead ?? message.IsRead;
		message.IsFlagged = change.IsFlagged ?? message.IsFlagged;
		occurrence.ImapModSeq = change.ImapModSeq ?? occurrence.ImapModSeq;
	}
}
