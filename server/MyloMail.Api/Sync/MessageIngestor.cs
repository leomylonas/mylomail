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

		// Batched up front rather than once per message (was up to ~3 queries per message plus
		// one per occurrence): a page's worth of messages is exactly the shape IMAP's
		// whole-mailbox-every-poll model produces, and per-item matching queries made every
		// steady-state poll of an established mailbox as expensive as its initial backfill.
		var (byStableId, byOccurrence, byHeader) = await LoadMatchCandidatesAsync(
			account,
			messages,
			mailboxesByProviderId,
			ct
		);

		var existingMemberships = await LoadExistingMembershipsAsync(
			messages,
			mailboxesByProviderId,
			byStableId,
			byOccurrence,
			byHeader,
			ct
		);

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
					&& mailbox.EffectiveSpecialUse == SpecialUse.Drafts
				)
			)
			{
				continue;
			}

			var message = Match(dto, mailboxesByProviderId, byStableId, byOccurrence, byHeader);
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

				membershipChanged |= await UpsertOccurrenceAsync(
					message,
					mailbox,
					occurrence,
					existingMemberships,
					ct
				);
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
	/// Loads every row this page's matching could possibly need, in three queries total
	/// regardless of page size. Deliberately over-fetches (e.g. an occurrence row for a
	/// mailbox/id combination no dto actually asked about) rather than trying to build an
	/// exact per-pair filter — the surplus rows are cheap and never looked up, whereas an exact
	/// filter would need a per-message OR clause EF Core can't batch any better than the
	/// original per-item queries.
	/// </summary>
	private async Task<(
		Dictionary<string, Message> ByStableId,
		Dictionary<(Guid MailboxId, string OccurrenceId), Message> ByOccurrence,
		ILookup<string, Message> ByHeader
	)> LoadMatchCandidatesAsync(
		Account account,
		IReadOnlyList<MessageDto> messages,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		CancellationToken ct
	)
	{
		var stableIds = messages
			.Select(m => m.ProviderStableId)
			.Where(id => id is not null)
			.Distinct()
			.ToList();
		var byStableIdRows = stableIds.Count == 0
			? []
			: await context
				.Messages.Where(m => m.AccountId == account.Id && stableIds.Contains(m.ProviderStableId))
				.ToListAsync(ct);
		// First-found-wins, same as the original per-item FirstOrDefaultAsync: this is a
		// pre-existing ambiguity (nothing enforces ProviderStableId uniqueness) this pass
		// doesn't change, just preserves.
		var byStableId = byStableIdRows
			.GroupBy(m => m.ProviderStableId!)
			.ToDictionary(g => g.Key, g => g.First());

		var mailboxIds = mailboxesByProviderId.Values.Select(m => m.Id).ToList();
		var occurrenceIds = messages
			.SelectMany(m => m.Occurrences)
			.Select(o => o.ProviderOccurrenceId)
			.Distinct()
			.ToList();
		var byOccurrenceRows =
			mailboxIds.Count == 0 || occurrenceIds.Count == 0
				? []
				: await context
					.MessageMailboxes.Where(o =>
						mailboxIds.Contains(o.MailboxId) && occurrenceIds.Contains(o.ProviderOccurrenceId)
					)
					.Join(
						context.Messages.Where(m => m.AccountId == account.Id),
						o => o.MessageId,
						m => m.Id,
						(o, m) => new { o.MailboxId, o.ProviderOccurrenceId, Message = m }
					)
					.ToListAsync(ct);
		var byOccurrence = byOccurrenceRows
			.GroupBy(r => (r.MailboxId, r.ProviderOccurrenceId))
			.ToDictionary(g => g.Key, g => g.First().Message);

		var headers = messages
			.Select(m => m.MessageIdHeader)
			.Where(h => h is not null)
			.Distinct()
			.ToList();
		var byHeaderRows = headers.Count == 0
			? []
			: await context
				.Messages.Where(m => m.AccountId == account.Id && headers.Contains(m.MessageIdHeader))
				.ToListAsync(ct);
		var byHeader = byHeaderRows.ToLookup(m => m.MessageIdHeader!);

		return (byStableId, byOccurrence, byHeader);
	}

	/// <summary>
	/// The matching precedence from §1, in order: the provider's stable id where it supplies
	/// one, then the per-mailbox occurrence identity, then
	/// <c>(AccountId, MessageIdHeader, ReceivedAt)</c> as a heuristic. Resolved entirely
	/// in-memory against <see cref="LoadMatchCandidatesAsync"/>'s preloaded rows.
	/// </summary>
	private static Message? Match(
		MessageDto dto,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		Dictionary<string, Message> byStableId,
		Dictionary<(Guid MailboxId, string OccurrenceId), Message> byOccurrence,
		ILookup<string, Message> byHeader
	)
	{
		if (dto.ProviderStableId is not null && byStableId.TryGetValue(dto.ProviderStableId, out var stableMatch))
		{
			return stableMatch;
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

			if (byOccurrence.TryGetValue((mailbox.Id, occurrence.ProviderOccurrenceId), out var occurrenceMatch))
			{
				return occurrenceMatch;
			}
		}

		// A Message-ID is metadata, not identity — RFC 5322 says SHOULD, not MUST, and
		// duplicates occur in practice — so it is paired with the received time and used only
		// as a last resort.
		if (dto.MessageIdHeader is not null)
		{
			return byHeader[dto.MessageIdHeader].FirstOrDefault(m => m.ReceivedAt == dto.ReceivedAt);
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
		if (dto.HasNonInlineAttachments is { } hasNonInlineAttachments)
		{
			message.HasNonInlineAttachments = hasNonInlineAttachments;
		}
		message.SizeEstimate = dto.SizeEstimate;
	}

	/// <summary>
	/// Preloads every <see cref="MessageMailbox"/> row a pre-existing (matched, not newly
	/// created) message in this page could already have in a mailbox this page touches — one
	/// query regardless of page size, so <see cref="UpsertOccurrenceAsync"/> only needs to fall
	/// back to a live query for a row this deliberately-broad preload still missed.
	/// </summary>
	private async Task<Dictionary<(Guid MessageId, Guid MailboxId), MessageMailbox>> LoadExistingMembershipsAsync(
		IReadOnlyList<MessageDto> messages,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId,
		Dictionary<string, Message> byStableId,
		Dictionary<(Guid MailboxId, string OccurrenceId), Message> byOccurrence,
		ILookup<string, Message> byHeader,
		CancellationToken ct
	)
	{
		var matchedMessageIds = messages
			.Select(dto => Match(dto, mailboxesByProviderId, byStableId, byOccurrence, byHeader)?.Id)
			.Where(id => id is not null)
			.Distinct()
			.ToList();
		var mailboxIds = mailboxesByProviderId.Values.Select(m => m.Id).ToList();

		if (matchedMessageIds.Count == 0 || mailboxIds.Count == 0)
		{
			return [];
		}

		var rows = await context
			.MessageMailboxes.Where(o =>
				matchedMessageIds.Contains(o.MessageId) && mailboxIds.Contains(o.MailboxId)
			)
			.ToListAsync(ct);
		return rows.ToDictionary(o => (o.MessageId, o.MailboxId));
	}

	/// <summary>Upserts one membership, reporting whether it actually changed anything.</summary>
	private async Task<bool> UpsertOccurrenceAsync(
		Message message,
		Mailbox mailbox,
		MessageOccurrenceDto dto,
		Dictionary<(Guid MessageId, Guid MailboxId), MessageMailbox> existingMemberships,
		CancellationToken ct
	)
	{
		// Any occurrence at all means this message is no longer a GC candidate — a late Graph
		// destination delta landing after collection started noticed it orphaned is exactly
		// the race §3/§6 require GC to survive.
		message.OrphanedAt = null;

		var existing =
			context
				.ChangeTracker.Entries<MessageMailbox>()
				.Select(e => e.Entity)
				.FirstOrDefault(o => o.MessageId == message.Id && o.MailboxId == mailbox.Id)
			?? existingMemberships.GetValueOrDefault((message.Id, mailbox.Id))
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
	/// Removes a batch of memberships from one mailbox. Never the canonical message: under
	/// Graph's folder-scoped delta a move surfaces as a removal and an addition in either
	/// order, so a canonical message may transiently have zero memberships (§3).
	/// </summary>
	/// <remarks>
	/// One query for the whole batch rather than one per occurrence — both callers (periodic
	/// integrity reconciliation and the live change stream) can report every expunge in a
	/// mailbox on a single page, and a large mailbox made this the dominant cost.
	/// </remarks>
	public async Task RemoveOccurrencesAsync(
		Mailbox mailbox,
		IReadOnlyList<string> providerOccurrenceIds,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		// A removal issued against a previous incarnation of this mailbox would delete a real
		// occurrence belonging to the new one — silently, since the provider occurrence id can
		// legitimately repeat across incarnations.
		if (providerOccurrenceIds.Count == 0 || !generations.StillCurrent(mailbox.ProviderMailboxId, mailbox))
		{
			return;
		}

		var occurrences = await context
			.MessageMailboxes.Where(o => o.MailboxId == mailbox.Id && providerOccurrenceIds.Contains(o.ProviderOccurrenceId))
			.ToListAsync(ct);

		context.MessageMailboxes.RemoveRange(occurrences);
	}

	/// <summary>Applies a batch of server-observed flag changes to server-known state.</summary>
	/// <remarks>
	/// Batched for the same reason as <see cref="RemoveOccurrencesAsync"/>. Changes are applied
	/// in the order given, so a provider that reports the same occurrence twice in one page
	/// still ends up with the later change winning, matching the previous one-at-a-time
	/// behaviour.
	/// </remarks>
	public async Task<IReadOnlyList<Message>> ApplyFlagChangesAsync(
		Mailbox mailbox,
		IReadOnlyList<OccurrenceFlagChange> changes,
		GenerationSnapshot generations,
		CancellationToken ct = default
	)
	{
		if (changes.Count == 0 || !generations.StillCurrent(mailbox.ProviderMailboxId, mailbox))
		{
			return [];
		}

		var occurrenceIds = changes.Select(c => c.ProviderOccurrenceId).Distinct().ToList();
		var occurrences = await context
			.MessageMailboxes.Where(o => o.MailboxId == mailbox.Id && occurrenceIds.Contains(o.ProviderOccurrenceId))
			.ToDictionaryAsync(o => o.ProviderOccurrenceId, ct);

		var messageIds = occurrences.Values.Select(o => o.MessageId).Distinct().ToList();
		var messages = await context.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

		var changed = new List<Message>();
		foreach (var change in changes)
		{
			if (!occurrences.TryGetValue(change.ProviderOccurrenceId, out var occurrence))
			{
				continue;
			}
			if (!messages.TryGetValue(occurrence.MessageId, out var message))
			{
				continue;
			}

			message.IsRead = change.IsRead ?? message.IsRead;
			message.IsFlagged = change.IsFlagged ?? message.IsFlagged;
			occurrence.ImapModSeq = change.ImapModSeq ?? occurrence.ImapModSeq;
			changed.Add(message);
		}

		return changed;
	}
}
