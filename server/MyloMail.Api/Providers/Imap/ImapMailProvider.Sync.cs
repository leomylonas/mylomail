using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

public sealed partial class ImapMailProvider
{
	private const int SyncPageSize = 200;

	public async Task<InitialSyncPage> InitialSyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await OpenAsync(client, mailbox, FolderAccess.ReadOnly, ct);

		var query = mode switch
		{
			// A bound limits historical backfill, never future synchronisation (§3).
			InitialSyncMode.LastNMonths when bound is int months => SearchQuery.DeliveredAfter(
				DateTime.UtcNow.AddMonths(-months)
			),
			_ => SearchQuery.All,
		};

		var matching = (await folder.SearchAsync(query, ct)).OrderByDescending(uid => uid.Id).ToList();

		if (mode == InitialSyncMode.LastNMessages && bound is int count)
		{
			matching = matching.Take(count).ToList();
		}

		var offset = int.TryParse(resumeToken, out var parsed) ? parsed : 0;
		var page = matching.Skip(offset).Take(pageSize).ToList();
		var consumed = offset + page.Count;
		var hasMore = consumed < matching.Count;

		var messages = await SummariseAsync(folder, page, ct);
		await client.DisconnectAsync(true, ct);

		return new InitialSyncPage(
			messages,
			hasMore ? consumed.ToString() : null,
			hasMore,
			matching.Count
		);
	}

	public async Task<SyncResult> SyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		ProviderCursorState? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await OpenAsync(client, mailbox, FolderAccess.ReadOnly, ct);

		var previous = cursor as ImapUidCursor;

		// UIDVALIDITY changes on a server-side reindex, which makes every stored UID
		// meaningless. One exception type so every provider's invalidation is handled once
		// rather than three times (§3).
		if (previous is not null && previous.UidValidity != folder.UidValidity)
		{
			throw new ProviderCursorInvalidException(
				$"UIDVALIDITY for '{folder.FullName}' changed from {previous.UidValidity} to {folder.UidValidity}"
			);
		}

		var since = previous?.HighestKnownUid ?? 0;
		var resumeFrom = uint.TryParse(continuation, out var parsed) ? parsed : since;

		var arrived = (
			await folder.SearchAsync(
				SearchQuery.Uids(new UniqueIdRange(new UniqueId(resumeFrom + 1), UniqueId.MaxValue)),
				ct
			)
		)
			.OrderBy(uid => uid.Id)
			.ToList();

		var page = arrived.Take(SyncPageSize).ToList();
		var more = arrived.Count > page.Count;

		var upserted = await SummariseAsync(folder, page, ct);
		var flagChanges = await FlagChangesAsync(folder, previous, ct);

		var highestSeen = page.Count > 0 ? page[^1].Id : resumeFrom;
		var cursorAfter = new ImapUidCursor(
			folder.UidValidity,
			highestSeen,
			folder.Supports(FolderFeature.ModSequences) ? folder.HighestModSeq : null,
			previous?.Reconciliation
		);

		await client.DisconnectAsync(true, ct);

		return new SyncResult(
			// Safe to commit even mid-walk: the high-water mark only ever covers UIDs already
			// returned, so a replay is possible but a skip is not.
			cursorAfter,
			more ? highestSeen.ToString() : null,
			upserted,
			flagChanges,
			// Expunge detection is UID-set reconciliation below QRESYNC and belongs with the
			// reconciliation machinery in stage C, not in a thin read path.
			[]
		);
	}

	public async Task<MailboxIntegritySnapshot> GetMailboxIntegritySnapshotAsync(
		Account account,
		Mailbox mailbox,
		IReadOnlyList<MessageOccurrenceRef> knownOccurrences,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await OpenAsync(client, mailbox, FolderAccess.ReadOnly, ct);
		var uids = await folder.SearchAsync(SearchQuery.All, ct);
		var existing = uids.Select(uid => uid.Id.ToString()).ToHashSet(StringComparer.Ordinal);

		// On the weakest tier the periodic pass is also the only honest source of flag
		// changes. CONDSTORE already supplies CHANGEDSINCE through the fast stream.
		IReadOnlyList<OccurrenceFlagChange> flags = [];
		if (!capabilities.SupportsIncrementalFlagChanges && uids.Count > 0)
		{
			var summaries = await folder.FetchAsync(
				uids,
				MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.ModSeq,
				ct
			);
			flags =
			[
				.. summaries.Select(summary => new OccurrenceFlagChange(
					folder.FullName,
					summary.UniqueId.Id.ToString(),
					(summary.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Seen),
					(summary.Flags ?? MessageFlags.None).HasFlag(MessageFlags.Flagged),
					(long?)summary.ModSeq
				)),
			];
		}

		await client.DisconnectAsync(true, ct);
		return new MailboxIntegritySnapshot(existing, flags);
	}

	private async Task<IReadOnlyList<OccurrenceFlagChange>> FlagChangesAsync(
		IMailFolder folder,
		ImapUidCursor? previous,
		CancellationToken ct
	)
	{
		// CHANGEDSINCE needs CONDSTORE. On the weakest tier flags come from a cadenced scan
		// over known UIDs, which the caller schedules — there is nothing incremental to report
		// here, and pretending otherwise would hide staleness the design states honestly (§3).
		if (
			!capabilities.SupportsIncrementalFlagChanges
			|| previous?.HighestModSeq is not ulong modSeq
			|| !folder.Supports(FolderFeature.ModSequences)
		)
		{
			return [];
		}

		var changed = await folder.FetchAsync(
			0,
			-1,
			modSeq,
			MessageSummaryItems.UniqueId | MessageSummaryItems.Flags,
			ct
		);

		return
		[
			.. changed
				.Where(summary => summary.Flags.HasValue)
				.Select(summary => new OccurrenceFlagChange(
					folder.FullName,
					summary.UniqueId.Id.ToString(),
					summary.Flags!.Value.HasFlag(MessageFlags.Seen),
					summary.Flags!.Value.HasFlag(MessageFlags.Flagged),
					(long?)summary.ModSeq
				)),
		];
	}

	private static async Task<IReadOnlyList<MessageDto>> SummariseAsync(
		IMailFolder folder,
		IList<UniqueId> uids,
		CancellationToken ct
	)
	{
		if (uids.Count == 0)
		{
			return [];
		}

		var summaries = await folder.FetchAsync(
			uids,
			MessageSummaryItems.UniqueId
				| MessageSummaryItems.Envelope
				| MessageSummaryItems.Flags
				| MessageSummaryItems.InternalDate
				| MessageSummaryItems.Size
				| MessageSummaryItems.ModSeq,
			ct
		);

		return [.. summaries.Select(summary => ToDto(folder, summary))];
	}

	private static MessageDto ToDto(IMailFolder folder, IMessageSummary summary)
	{
		var envelope = summary.Envelope;
		var flags = summary.Flags ?? MessageFlags.None;

		return new MessageDto
		{
			// IMAP has no account-wide stable message identifier at all (§1).
			ProviderStableId = null,
			Occurrences =
			[
				new MessageOccurrenceDto(
					folder.FullName,
					summary.UniqueId.Id.ToString(),
					(long?)summary.ModSeq
				),
			],
			MessageIdHeader = envelope?.MessageId,
			InReplyToHeader = envelope?.InReplyTo,
			ReplyToAddresses = Addresses(envelope?.ReplyTo),
			From = Addresses(envelope?.From),
			To = Addresses(envelope?.To),
			Cc = Addresses(envelope?.Cc),
			Bcc = Addresses(envelope?.Bcc),
			Subject = envelope?.Subject ?? string.Empty,
			ReceivedAt = summary.InternalDate ?? envelope?.Date ?? DateTimeOffset.MinValue,
			IsRead = flags.HasFlag(MessageFlags.Seen),
			IsFlagged = flags.HasFlag(MessageFlags.Flagged),
			IsDraft = flags.HasFlag(MessageFlags.Draft),

			// Readable metadata only. \Answered has no portable mutable equivalent, which is
			// why FlagUpdate omits it (§2).
			IsAnswered = flags.HasFlag(MessageFlags.Answered),

			SizeEstimate = summary.Size.HasValue ? (long)summary.Size.Value : null,
		};
	}

	private static IReadOnlyList<Address> Addresses(InternetAddressList? list) =>
		list is null
			? []
			: [.. list.Mailboxes.Select(m => new Address(m.Name, m.Address))];

	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await client.GetFolderAsync(
			mailboxes.ProviderMailboxId(occurrence.MailboxId),
			ct
		);
		await folder.OpenAsync(FolderAccess.ReadOnly, ct);

		// BODY.PEEK[] — one fetch yields body, headers, attachments and search content, and
		// PEEK is what keeps reading a message from marking it read (§1).
		using var stream = await folder.GetStreamAsync(
			new UniqueId(uint.Parse(occurrence.ProviderOccurrenceId)),
			string.Empty,
			ct
		);
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, ct);

		await client.DisconnectAsync(true, ct);
		return new RawMessageResult(buffer.ToArray());
	}
}
