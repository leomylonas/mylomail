using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contacts;
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
/// <param name="CountedMailboxIds">
/// Every mailbox whose membership set this ingest actually added a row to, so the caller can
/// announce the mailboxes whose counts moved (§7). A membership whose provider occurrence id
/// or <c>MODSEQ</c> was merely rewritten is excluded: the count is identical, and a mailbox
/// event that fires when nothing a listener can see changed tells it nothing. An
/// account-scoped page reports several mailboxes at once, so the mailbox that happened to be
/// polled is not the set of mailboxes it changed.
/// </param>
public sealed record IngestResult(
	IReadOnlyList<Message> Created,
	IReadOnlyList<Message> Updated,
	IReadOnlyList<Message> Observed,
	IReadOnlyList<Message> Rethreaded,
	bool ContactSuggestionsChanged,
	IReadOnlyList<Guid> CountedMailboxIds
);

/// <summary>
/// Turns provider observations into local rows. Every write here is an upsert, which is what
/// makes replaying a page safe — and replay is the price of never skipping one (§3).
/// </summary>
public sealed class MessageIngestor(
	MyloMailDbContext context,
	ContactSuggestionService contactSuggestions
)
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
		var counted = new HashSet<Guid>();

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
		var threadCandidates = await LoadThreadCandidatesAsync(account.Id, messages, ct);
		var pageMessages = new Dictionary<MessageDto, (Message Message, bool IsNew)>(
			ReferenceEqualityComparer.Instance
		);
		var pageCreatedIds = new HashSet<Guid>();
		foreach (var dto in messages)
		{
			// Draft observations never materialise as Message rows (§1). Exclude them before
			// matching so their fields cannot overwrite an unrelated message.
			if (IsDraftObservation(dto, mailboxesByProviderId)) continue;
			var message = Match(dto, mailboxesByProviderId, byStableId, byOccurrence, byHeader);
			var createsPageIdentity = message is null;
			if (message is null)
			{
				// Allocate every page-local identity before assigning threads. This makes
				// header cardinality independent of provider page order and prevents a child
				// from binding before a duplicate Message-ID later in the same page is known.
				message = new Message { Id = Guid.NewGuid(), AccountId = account.Id };
				context.Messages.Add(message);
				context.MessageContentStates.Add(
					new MessageContentState { MessageId = message.Id, Status = ContentStatus.Queued }
				);
			}
			if (createsPageIdentity) pageCreatedIds.Add(message.Id);
			var isNew = pageCreatedIds.Contains(message.Id);
			if (dto.ProviderStableId is not null)
				byStableId[dto.ProviderStableId] = message;
			foreach (var occurrence in dto.Occurrences)
			{
				if (mailboxesByProviderId.TryGetValue(
					occurrence.ProviderMailboxId,
					out var providerMailbox
				))
					byOccurrence[(providerMailbox.Id, occurrence.ProviderOccurrenceId)] = message;
			}
			pageMessages[dto] = (message, isNew);
			if (!string.IsNullOrWhiteSpace(dto.MessageIdHeader))
			{
				if (!threadCandidates.TryGetValue(dto.MessageIdHeader, out var sameHeader))
					threadCandidates.Add(dto.MessageIdHeader, sameHeader = []);
				if (sameHeader.All(candidate => candidate.Id != message.Id)) sameHeader.Add(message);
			}
		}
		var priorMessages = pageMessages.Values
			.Where(item => !item.IsNew)
			.Select(item => item.Message)
			.DistinctBy(message => message.Id)
			.ToArray();
		var priorThreadIds = priorMessages.ToDictionary(
			message => message.Id,
			message => message.ThreadId
		);
		var priorMessageIdHeaders = priorMessages
			.Select(message => message.MessageIdHeader)
			.OfType<string>()
			.ToArray();
		foreach (var (dto, resolved) in pageMessages)
			Apply(dto, resolved.Message);
		var contactSuggestionsChanged = await contactSuggestions.ObserveAsync(
			account.Id,
			pageMessages.Values
				.Select(item => item.Message)
				.DistinctBy(message => message.Id)
				.SelectMany(SuggestionAddresses),
			ct
		);
		threadCandidates = threadCandidates.Values
			.SelectMany(candidates => candidates)
			.Concat(pageMessages.Values.Select(item => item.Message))
			.DistinctBy(message => message.Id)
			.Where(message => !string.IsNullOrWhiteSpace(message.MessageIdHeader))
			.GroupBy(message => message.MessageIdHeader!, StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => group.ToList(),
				StringComparer.Ordinal
			);
		// Give new ancestors a stable local root before propagating their headers through
		// persisted descendants; assign once more afterward so new descendants observe that
		// propagated root in the same page.
		AssignFallbackThreads(pageMessages, threadCandidates);
		var changedHeaders = priorMessageIdHeaders.Concat(pageMessages.Values
			.Select(item => item.Message.MessageIdHeader)
			.OfType<string>());
		var propagatedRethreads = await RecomputeFallbackThreadsAsync(
			account.Id,
			changedHeaders,
			ct
		);
		AssignFallbackThreads(pageMessages, threadCandidates);
		var rethreaded = pageMessages.Values
			.Select(item => item.Message)
			.Where(message => priorThreadIds.TryGetValue(message.Id, out var priorThreadId)
				&& priorThreadId != message.ThreadId)
			.Concat(propagatedRethreads)
			.DistinctBy(message => message.Id)
			.ToArray();


		foreach (var dto in messages)
		{
			if (!pageMessages.TryGetValue(dto, out var resolved)) continue;
			var (message, isNew) = resolved;
			var membershipChanged = false;

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

				var outcome = await UpsertOccurrenceAsync(
					message,
					mailbox,
					occurrence,
					existingMemberships,
					ct
				);
				membershipChanged |= outcome != MembershipOutcome.Unchanged;
				if (outcome == MembershipOutcome.Added)
				{
					counted.Add(mailbox.Id);
				}
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

		return new IngestResult(
			created.DistinctBy(message => message.Id).ToArray(),
			updated.DistinctBy(message => message.Id).ToArray(),
			observed.DistinctBy(message => message.Id).ToArray(),
			rethreaded,
			contactSuggestionsChanged,
			[.. counted]
		);
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

	private static bool IsDraftObservation(
		MessageDto message,
		IReadOnlyDictionary<string, Mailbox> mailboxesByProviderId
	) => message.Occurrences.Any(occurrence =>
		mailboxesByProviderId.TryGetValue(occurrence.ProviderMailboxId, out var mailbox)
		&& mailbox.EffectiveSpecialUse == SpecialUse.Drafts);

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
		message.MessageIdHeader = dto.MessageIdHeader ?? message.MessageIdHeader;
		message.InReplyToHeader = dto.InReplyToHeader ?? message.InReplyToHeader;
		message.ReferencesHeader = dto.ReferencesHeader ?? message.ReferencesHeader;
		message.ReplyToAddresses = dto.ReplyToAddresses;
		message.SenderAddress = dto.SenderAddress;
		message.HasProviderThreadId = !string.IsNullOrWhiteSpace(dto.ThreadId);
		if (message.HasProviderThreadId) message.ThreadId = dto.ThreadId;
		message.From = dto.From;
		message.To = dto.To;
		message.Cc = dto.Cc;
		message.Bcc = dto.Bcc;
		message.Subject = dto.Subject;
		// An IMAP envelope has no preview, so replaying or polling it after content acquisition
		// must not erase the body-derived snippet already committed locally. Provider previews
		// are still authoritative when present (Graph/Gmail), and a new blank message remains
		// blank.
		if (!string.IsNullOrWhiteSpace(dto.Snippet) || string.IsNullOrWhiteSpace(message.Snippet))
		{
			message.Snippet = dto.Snippet;
		}
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


	private static IEnumerable<Address> SuggestionAddresses(Message message)
	{
		foreach (var address in message.From) yield return address;
		foreach (var address in message.To) yield return address;
		foreach (var address in message.Cc) yield return address;
		foreach (var address in message.Bcc) yield return address;
		foreach (var address in message.ReplyToAddresses) yield return address;
		if (message.SenderAddress is not null) yield return message.SenderAddress;
	}

	/// <summary>
	/// Resolves RFC fallback ancestry as a page-local graph before writing any thread. A
	/// provider may return descendants before ancestors; recursive root resolution keeps the
	/// result independent of that order. Ambiguous or missing headers deliberately form a
	/// separate local conversation because an incorrect merge is irrecoverable.
	/// </summary>
	private static void AssignFallbackThreads(
		IReadOnlyDictionary<MessageDto, (Message Message, bool IsNew)> pageMessages,
		IReadOnlyDictionary<string, List<Message>> candidates
	)
	{
		var resolved = new Dictionary<Guid, string>();
		var recomputed = pageMessages.Values
			.Select(item => item.Message.Id)
			.ToHashSet();
		foreach (var message in pageMessages.Values.Select(item => item.Message).DistinctBy(item => item.Id))
			message.ThreadId = ResolveFallbackThread(message, candidates, resolved, [], recomputed);
	}

	private static string ResolveFallbackThread(
		Message message,
		IReadOnlyDictionary<string, List<Message>> candidates,
		Dictionary<Guid, string> resolved,
		HashSet<Guid> path,
		IReadOnlySet<Guid> recomputed
	)
	{
		if (message.HasProviderThreadId && !string.IsNullOrWhiteSpace(message.ThreadId))
			return message.ThreadId;
		if (!recomputed.Contains(message.Id) && !string.IsNullOrWhiteSpace(message.ThreadId))
			return message.ThreadId;
		if (resolved.TryGetValue(message.Id, out var threadId)) return threadId;
		if (!path.Add(message.Id))
			return $"local:{path.Min():N}";

		foreach (var header in ParentHeaders(message))
		{
			if (!candidates.TryGetValue(header, out var parents)
				|| parents.Count != 1
				|| parents[0].Id == message.Id) continue;
			threadId = ResolveFallbackThread(parents[0], candidates, resolved, path, recomputed);
			path.Remove(message.Id);
			return resolved[message.Id] = threadId;
		}

		path.Remove(message.Id);
		return resolved[message.Id] = $"local:{message.Id:N}";
	}
	internal async Task<IReadOnlyList<Message>> RecomputeFallbackThreadsAsync(
		Guid accountId,
		IEnumerable<string> changedHeaders,
		CancellationToken ct
	)
	{
		var changed = new List<Message>();
		var frontier = changedHeaders
			.Where(header => !string.IsNullOrWhiteSpace(header))
			.Distinct(StringComparer.Ordinal)
			.ToList();
		while (frontier.Count > 0)
		{
			var headers = frontier.ToArray();
			var headersJson = JsonSerializer.Serialize(headers);
			var descendants = (await context.Messages.FromSqlInterpolated($"""
				SELECT *
				FROM "Messages" AS "m"
				WHERE "m"."AccountId" = {accountId}
					AND EXISTS (
						SELECT 1
						FROM json_each({headersJson}) AS "h"
						WHERE instr(COALESCE("m"."InReplyToHeader", ''), "h"."value") > 0
							OR instr(COALESCE("m"."ReferencesHeader", ''), "h"."value") > 0
					)
				""").ToListAsync(ct))
				.Where(message => ParentHeaders(message).Any(headers.Contains))
				.ToList();
			if (descendants.Count == 0) break;

			var referencedHeaders = descendants.SelectMany(ParentHeaders)
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			var persistedParents = await context.Messages
				.Where(message => message.AccountId == accountId
					&& message.MessageIdHeader != null
					&& referencedHeaders.Contains(message.MessageIdHeader))
				.ToListAsync(ct);
			var parents = persistedParents
				.Concat(context.ChangeTracker.Entries<Message>()
					.Select(entry => entry.Entity)
					.Where(message => message.AccountId == accountId
						&& message.MessageIdHeader is not null
						&& referencedHeaders.Contains(message.MessageIdHeader)))
				.DistinctBy(message => message.Id)
				.GroupBy(message => message.MessageIdHeader!, StringComparer.Ordinal)
				.ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

			var next = new List<string>();
			foreach (var descendant in descendants)
			{
				if (descendant.HasProviderThreadId) continue;
				var parent = ParentHeaders(descendant)
					.Select(header => parents.GetValueOrDefault(header))
					.Where(candidates => candidates is { Length: 1 })
					.Select(candidates => candidates![0])
					.FirstOrDefault(candidate => candidate.Id != descendant.Id
						&& !string.IsNullOrEmpty(candidate.ThreadId));
				var threadId = parent?.ThreadId ?? $"local:{descendant.Id:N}";
				if (descendant.ThreadId == threadId) continue;
				descendant.ThreadId = threadId;
				changed.Add(descendant);
				if (!string.IsNullOrWhiteSpace(descendant.MessageIdHeader))
					next.Add(descendant.MessageIdHeader);
			}
			frontier = next.Distinct(StringComparer.Ordinal).ToList();
		}
		return changed;
	}
	internal async Task RebuildFallbackThreadsAsync(Guid accountId, CancellationToken ct)
	{
		var messages = await context.Messages
			.Where(message => message.AccountId == accountId)
			.ToListAsync(ct);
		var candidates = messages
			.Where(message => !string.IsNullOrWhiteSpace(message.MessageIdHeader))
			.GroupBy(message => message.MessageIdHeader!, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
		var resolved = new Dictionary<Guid, string>();
		var recomputed = messages.Select(message => message.Id).ToHashSet();
		foreach (var message in messages.Where(message => !message.HasProviderThreadId))
			message.ThreadId = ResolveFallbackThread(message, candidates, resolved, [], recomputed);
		await context.SaveChangesAsync(ct);
	}



	private async Task<Dictionary<string, List<Message>>> LoadThreadCandidatesAsync(
		Guid accountId,
		IReadOnlyList<MessageDto> messages,
		CancellationToken ct
	)
	{
		var headers = messages.SelectMany(ParentHeaders).Distinct(StringComparer.Ordinal).ToList();
		if (headers.Count == 0) return [];
		return (await context.Messages
			.Where(message => message.AccountId == accountId
				&& message.MessageIdHeader != null
				&& headers.Contains(message.MessageIdHeader))
			.ToListAsync(ct))
			.GroupBy(message => message.MessageIdHeader!, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
	}

	private static IEnumerable<string> ParentHeaders(MessageDto message)
	{
		foreach (var header in ParseMessageIds(message.InReplyToHeader)) yield return header;
		foreach (var header in ParseMessageIds(message.ReferencesHeader).Reverse()) yield return header;
	}
	private static IEnumerable<string> ParentHeaders(Message message)
	{
		foreach (var header in ParseMessageIds(message.InReplyToHeader)) yield return header;
		foreach (var header in ParseMessageIds(message.ReferencesHeader).Reverse()) yield return header;
	}

	private static IReadOnlyList<string> ParseMessageIds(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return [];
		var matches = Regex.Matches(value, "<[^<>]+>");
		return matches.Count == 0
			? [value.Trim()]
			: matches.Select(match => match.Value).ToArray();
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

	/// <summary>What one membership upsert did, which is not the same question as whether it changed a row.</summary>
	/// <remarks>
	/// A rewritten provider occurrence id changes the row without changing the mailbox's
	/// count; only <see cref="Added"/> moves a count (§7).
	/// </remarks>
	private enum MembershipOutcome
	{
		Unchanged,
		Rewritten,
		Added,
	}

	/// <summary>Upserts one membership, reporting what it actually did.</summary>
	private async Task<MembershipOutcome> UpsertOccurrenceAsync(
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
			return MembershipOutcome.Added;
		}

		existing.ProviderOccurrenceId = dto.ProviderOccurrenceId;
		existing.ImapModSeq = dto.ImapModSeq ?? existing.ImapModSeq;
		return context.Entry(existing).State == EntityState.Modified
			? MembershipOutcome.Rewritten
			: MembershipOutcome.Unchanged;
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
	public async Task<IReadOnlyList<Guid>> RemoveOccurrencesAsync(
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
			return [];
		}

		var occurrences = await context
			.MessageMailboxes.Where(o => o.MailboxId == mailbox.Id && providerOccurrenceIds.Contains(o.ProviderOccurrenceId))
			.ToListAsync(ct);

		context.MessageMailboxes.RemoveRange(occurrences);
		return [.. occurrences.Select(occurrence => occurrence.MessageId).Distinct()];
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
