using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Text;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Sync;

namespace MyloMail.Api.Content;

public enum ContentAcquisitionResult
{
	Stored,
	Deferred,
}

/// <summary>
/// Fetches a message's raw MIME and derives everything else from it (§1, §8).
/// </summary>
/// <remarks>
/// <para>
/// <b>One fetch per message, not one per concern.</b> Gmail's <c>format=RAW</c>, Graph's
/// <c>$value</c> and IMAP's <c>BODY.PEEK[]</c> each return the whole message in a single
/// call, so headers, bodies, attachment metadata and search text are all parsed from that one
/// result rather than fetched separately.
/// </para>
/// <para>
/// <b>Bodies are not fetched lazily on open.</b> Search has to cover mail the user has never
/// opened (§13, Epic 5), which lazy fetching cannot deliver — so this runs as background work
/// after coverage, at lower priority than metadata sync so an account becomes usable first.
/// </para>
/// </remarks>
public sealed class ContentAcquisition(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	SearchIndexer search,
	MailInviteMaterializer invites,
	IHubEvents events,
	ConnectivityMonitor connectivity,
	IFaultInjector faults,
	ILogger<ContentAcquisition> logger
)
{
	/// <summary>
	/// A single failure is treated as a transient blip — network flakiness mid-fetch, say —
	/// and retried. Only after this many consecutive failures is the message given up on as
	/// permanently unreadable, since a fetch that keeps failing on the same bytes would
	/// otherwise be retried by <see cref="Scheduling.ContentJobs"/> forever.
	/// </summary>
	internal const int MaxAttempts = 5;
	internal const int MaximumRawMessageBytes = 64 * 1024 * 1024;

	/// <summary>Fetches and stores one message's content.</summary>
	public async Task<ContentAcquisitionResult> AcquireAsync(
		Account account,
		Guid messageId,
		CancellationToken ct = default
	)
	{
		if (!connectivity.IsOnline)
		{
			throw new HttpRequestException("Content is unavailable while the app is offline.");
		}
		var state = await context.MessageContentStates.FirstOrDefaultAsync(c => c.MessageId == messageId, ct);
		if (state is null)
		{
			state = new MessageContentState { MessageId = messageId, Status = ContentStatus.NotFetched };
			context.MessageContentStates.Add(state);
		}

		var issued = await context
			.MessageMailboxes.Where(o => o.MessageId == messageId)
			.Join(
				context.Mailboxes.Where(m => m.AccountId == account.Id),
				o => o.MailboxId,
				m => m.Id,
				(o, m) =>
					new ContentFetchSnapshot(
						o.Id,
						o.MailboxId,
						o.ProviderOccurrenceId,
						m.TopologyGeneration
					)
			)
			.FirstOrDefaultAsync(ct);
		if (issued is null)
		{
			// No membership means nothing addressable on the server. A message can legitimately
			// be in this state for a moment during a move (§3), so it is left alone rather
			// than failed.
			return ContentAcquisitionResult.Deferred;
		}

		state.Status = ContentStatus.Fetching;
		state.Attempts++;
		await context.SaveChangesAsync(ct);

		RawMessageResult raw;
		try
		{
			raw = await providers
				.For(account)
				.FetchRawMessageAsync(
					account,
					new MessageOccurrenceRef(messageId, issued.MailboxId, issued.ProviderOccurrenceId),
					ct,
					MaximumRawMessageBytes
				);
		}
		catch (ProviderThrottledException ex)
		{
			// Rate limiting says nothing about this message's readability. Undo this fetch
			// attempt while preserving the issued-occurrence fence, then let the scheduler
			// apply the provider's exact account-wide delay.
			await RecordFailureAsync(
				state,
				issued,
				messageId,
				ex,
				consumeAttempt: false,
				clearTracker: false,
				ct
			);
			throw;
		}
		catch (Credentials.CredentialStoreUnavailableException ex)
		{
			// Not evidence the content is unreadable -- nothing about this message was even
			// attempted, providers.For(account) failed before it could dial out. Left uncounted
			// against MaxAttempts so a sustained local credential-store outage can never
			// exhaust the retry budget and mislabel readable content as permanently Failed.
			if (
				await RecordFailureAsync(
					state,
					issued,
					messageId,
					ex,
					consumeAttempt: false,
					clearTracker: false,
					ct
				)
			)
			{
				return ContentAcquisitionResult.Deferred;
			}
			logger.LogWarning(ex, "Content fetch for message {MessageId} could not reach the credential store.", messageId);
			throw;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			await connectivity.MarkOfflineAsync();
			// A sustained outage recurs on every fetch while it lasts, and the message was
			// never actually unreadable — only unreachable. Leave it uncounted against
			// MaxAttempts. The failure write is fenced with the issued occurrence so an old
			// mailbox cannot exhaust the retry budget of its replacement.
			if (
				await RecordFailureAsync(
					state,
					issued,
					messageId,
					ex,
					consumeAttempt: false,
					clearTracker: false,
					ct
				)
			)
			{
				return ContentAcquisitionResult.Deferred;
			}
			logger.LogDebug(ex, "Content fetch for message {MessageId} could not reach the network.", messageId);
			throw;
		}
		catch (Exception ex)
		{
			// Requeued rather than failed outright while attempts remain: a transient blip
			// must not read as permanently malformed content. The current-occurrence check
			// and this failure state commit atomically.
			if (
				await RecordFailureAsync(
					state,
					issued,
					messageId,
					ex,
					consumeAttempt: true,
					clearTracker: false,
					ct
				)
			)
			{
				return ContentAcquisitionResult.Deferred;
			}
			logger.LogWarning(ex, "Content fetch failed for message {MessageId}.", messageId);
			throw;
		}

		MimeMessage? mime;
		try
		{
			mime = await StoreAsync(state, issued, messageId, raw.RawBytes, ct);
		}
		catch (Exception ex) when (ex is not SimulatedCrashException)
		{
			// Parsing or storage failed, which will fail again on the same bytes. The failed
			// content transaction may have left tracked entities inconsistent, so recovery
			// reloads fresh state before atomically checking the issued occurrence.
			if (
				await RecordFailureAsync(
					state,
					issued,
					messageId,
					ex,
					consumeAttempt: true,
					clearTracker: true,
					ct
				)
			)
			{
				return ContentAcquisitionResult.Deferred;
			}
			throw;
		}

		if (mime is null)
		{
			return ContentAcquisitionResult.Deferred;
		}

		// After the content transaction, and only for a fetch that actually stored something:
		// §7 pairs SyncProgress with each content-index page as much as with each backfill
		// page, and indexing runs long after coverage reports complete.
		await ContentProgressAnnouncer.AnnounceAsync(context, events, messageId, ct);

		// Deliberately after the content transaction has committed, not inside it: calendar
		// materialisation is a different coordination domain (§6), and a hiccup there must
		// never roll back an otherwise-successful, precious content fetch (retries are capped
		// at MaxAttempts). A failure here is logged and swallowed — the message body is still
		// usable even if its invite never gets picked up this pass.
		try
		{
			await invites.MaterializeFromMessageAsync(account, messageId, mime, ct);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Invite materialisation failed for message {MessageId}.", messageId);
		}

		return ContentAcquisitionResult.Stored;
	}

	private async Task<bool> RecordFailureAsync(
		MessageContentState state,
		ContentFetchSnapshot issued,
		Guid messageId,
		Exception ex,
		bool consumeAttempt,
		bool clearTracker,
		CancellationToken ct
	)
	{
		if (clearTracker)
		{
			context.ChangeTracker.Clear();
			var persistedState = await context.MessageContentStates.SingleOrDefaultAsync(
				c => c.MessageId == messageId,
				ct
			);
			if (persistedState is null)
			{
				return false;
			}
			state = persistedState;
		}

		var stale = false;
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			if (!await FetchStillCurrentAsync(issued, messageId, ct))
			{
				state.Attempts = Math.Max(0, state.Attempts - 1);
				state.Status = ContentStatus.Queued;
				state.LastError = null;
				stale = true;
			}
			else
			{
				if (!consumeAttempt)
				{
					state.Attempts = Math.Max(0, state.Attempts - 1);
				}
				state.Status =
					consumeAttempt && state.Attempts >= MaxAttempts
						? ContentStatus.Failed
						: ContentStatus.Queued;
				state.LastError = ex.Message;
			}

			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});
		return stale;
	}

	private Task<bool> FetchStillCurrentAsync(
		ContentFetchSnapshot issued,
		Guid messageId,
		CancellationToken ct
	) =>
		context
			.MessageMailboxes.Where(o =>
				o.Id == issued.OccurrenceId
				&& o.MessageId == messageId
				&& o.MailboxId == issued.MailboxId
				&& o.ProviderOccurrenceId == issued.ProviderOccurrenceId
			)
			.Join(
				context.Mailboxes.Where(m =>
					m.Id == issued.MailboxId && m.TopologyGeneration == issued.TopologyGeneration
				),
				o => o.MailboxId,
				m => m.Id,
				(_, _) => true
			)
			.AnyAsync(ct);

	/// <summary>
	/// Stores the raw bytes and everything parsed from them, in one transaction.
	/// </summary>
	/// <remarks>
	/// The search row is written here rather than by a trigger, in the same transaction as the
	/// content it describes: a committed body with no index entry is invisible to search
	/// forever, and an index entry with no body is a hit pointing at nothing (§8).
	/// </remarks>
	private async Task<MimeMessage?> StoreAsync(
		MessageContentState state,
		ContentFetchSnapshot issued,
		Guid messageId,
		byte[] rawBytes,
		CancellationToken ct
	)
	{
		using var stream = new MemoryStream(rawBytes);
		var mime = await MimeMessage.LoadAsync(stream, ct);
		MimeStructureValidator.Validate(mime);

		Message? messageForBroadcast = null;
		var hasNonInlineAttachmentsChanged = false;
		var snippetChanged = false;

		var discarded = false;
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			var stillCurrent = await FetchStillCurrentAsync(issued, messageId, ct);
			if (!stillCurrent)
			{
				// The provider result belongs to a mailbox incarnation that no longer exists.
				// It says nothing about whether the message is readable through a current
				// occurrence, so discard it without consuming a content retry.
				state.Attempts = Math.Max(0, state.Attempts - 1);
				state.Status = ContentStatus.Queued;
				state.LastError = null;
				await context.SaveChangesAsync(ct);
				await transaction.CommitAsync(ct);
				discarded = true;
				return;
			}

			await UpsertRawAsync(messageId, rawBytes, ct);
			await UpsertHeadersAsync(messageId, mime, ct);
			var body = await UpsertBodyAsync(messageId, mime, ct);

			// Incremented because the parts below are locators into *this* blob: a MIME part
			// path is not an eternal identity, so attachment rows are rebuilt whenever raw
			// content is replaced (§1).
			state.RawVersion++;
			await RebuildAttachmentsAsync(messageId, mime, state.RawVersion, ct);
			await search.IndexAsync(messageId, mime, body, ct);

			var message = await context.Messages.FirstAsync(m => m.Id == messageId, ct);
			message.RawFetched = true;

			// The ingest-time structural guess (IMAP's BODYSTRUCTURE, Gmail's Payload part tree)
			// is usually already right, so this only differs on a genuine disagreement — an
			// already-open message list has no other way to learn its guess was wrong (§7).
			var correctedHasNonInlineAttachments = mime.Attachments.Any();
			if (message.HasNonInlineAttachments != correctedHasNonInlineAttachments)
			{
				message.HasNonInlineAttachments = correctedHasNonInlineAttachments;
				hasNonInlineAttachmentsChanged = true;
				messageForBroadcast = message;
			}

			// IMAP's ToDto never sets MessageDto.Snippet at all (ENVELOPE carries no preview
			// text), so every IMAP message would show a permanently blank list preview otherwise
			// — docs/architecture.md §13 lists "snippet" as one of the message list's own sortable/
			// filterable columns. Only fills a genuinely blank snippet: Gmail/Graph already supply
			// their own provider-computed preview at ingest time, which is not second-guessed here.
			if (string.IsNullOrWhiteSpace(message.Snippet))
			{
				var computedSnippet = ComputeSnippet(body.TextBody, body.HtmlBody);
				if (!string.IsNullOrEmpty(computedSnippet))
				{
					message.Snippet = computedSnippet;
					snippetChanged = true;
					messageForBroadcast = message;
				}
			}

			state.Status = ContentStatus.Indexed;
			state.LastError = null;

			await context.SaveChangesAsync(ct);
			faults.Reached(FaultPoints.ContentAfterApplyBeforeCommit);
			await transaction.CommitAsync(ct);
		});

		if (discarded)
		{
			return null;
		}

		// After the commit, never before: an event announcing a correction a crash then
		// discarded would leave the UI showing something the database does not have.
		if ((hasNonInlineAttachmentsChanged || snippetChanged) && messageForBroadcast is not null)
		{
			await events.MessageUpdatedAsync(MessageEventMapper.ToSummary(messageForBroadcast));
		}

		return mime;
	}

	/// <summary>
	/// A short, whitespace-collapsed preview of the message body — plain text preferred, HTML
	/// stripped of tags as a fallback for an HTML-only message, matching the same simple
	/// tag-stripping approach already used for a plain-text send alternative
	/// (<c>ImapMailProvider.Send.cs</c>'s own <c>ToPlainText</c>).
	/// </summary>
	internal static string ComputeSnippet(string? text, string? html)
	{
		const int maxLength = 200;
		// An empty (not just null) plain-text part is a real MIME shape - a stub first
		// alternative alongside the real HTML-only content - so falling back on
		// IsNullOrEmpty, not a bare null check, actually reaches the HTML body in that case.
		var source = string.IsNullOrEmpty(text)
			? (html is null ? null : System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "))
			: text;
		if (string.IsNullOrWhiteSpace(source))
		{
			return string.Empty;
		}

		var collapsed = string.Join(
			' ',
			source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
		);
		if (collapsed.Length <= maxLength)
		{
			return collapsed;
		}

		// A raw code-unit slice can land inside a surrogate pair (an emoji, say); back off one
		// character rather than leave an unpaired low surrogate that renders as a replacement
		// character or corrupts the string downstream.
		var cut = maxLength;
		if (char.IsHighSurrogate(collapsed[cut - 1]))
		{
			cut--;
		}
		return collapsed[..cut];
	}

	private async Task UpsertRawAsync(Guid messageId, byte[] rawBytes, CancellationToken ct)
	{
		var existing = await context.MessageRaws.FirstOrDefaultAsync(r => r.MessageId == messageId, ct);
		if (existing is null)
		{
			context.MessageRaws.Add(new MessageRaw { MessageId = messageId, Content = rawBytes });
			return;
		}

		existing.Content = rawBytes;
	}

	private async Task UpsertHeadersAsync(Guid messageId, MimeMessage mime, CancellationToken ct)
	{
		// Every header, in received order and keeping duplicates: this is the diagnostic
		// record of what actually arrived, not a normalised view of it.
		IReadOnlyList<MessageHeader> headers =
		[
			.. mime.Headers.Select(header => new MessageHeader(header.Field, header.Value)),
		];

		var existing = await context.MessageHeaders.FirstOrDefaultAsync(h => h.MessageId == messageId, ct);
		if (existing is null)
		{
			context.MessageHeaders.Add(new MessageHeaders { MessageId = messageId, Headers = headers });
			return;
		}

		existing.Headers = headers;
	}

	private async Task<MessageBody> UpsertBodyAsync(Guid messageId, MimeMessage mime, CancellationToken ct)
	{
		var text = mime.GetTextBody(TextFormat.Plain);
		var html = mime.GetTextBody(TextFormat.Html);

		var existing = await context.MessageBodies.FirstOrDefaultAsync(b => b.MessageId == messageId, ct);
		if (existing is not null)
		{
			existing.TextBody = text;
			existing.HtmlBody = html;
			return existing;
		}

		var body = new MessageBody
		{
			MessageId = messageId,
			TextBody = text,
			HtmlBody = html,
		};
		context.MessageBodies.Add(body);
		return body;
	}

	private async Task RebuildAttachmentsAsync(
		Guid messageId,
		MimeMessage mime,
		int rawVersion,
		CancellationToken ct
	)
	{
		var stale = await context.Attachments.Where(a => a.MessageId == messageId).ToListAsync(ct);
		context.Attachments.RemoveRange(stale);

		// Walked with a MimeIterator because the locator stored is the part's *path* within
		// this blob — "2.1" — and only the iterator knows it. A bare part has no idea where
		// in the tree it sits.
		var iterator = new MimeIterator(mime);
		while (iterator.MoveNext())
		{
			if (iterator.Current is not MimePart part)
			{
				continue;
			}

			var isInline = part.ContentDisposition?.IsAttachment != true;
			if (isInline && part.ContentId is null && part.FileName is null)
			{
				// A body part rather than something the user would recognise as attached.
				continue;
			}

			context.Attachments.Add(
				new Attachment
				{
					Id = Guid.NewGuid(),
					MessageId = messageId,
					PartSpecifier = iterator.PathSpecifier,
					RawVersion = rawVersion,
					Filename = part.FileName ?? string.Empty,
					MimeType = part.ContentType.MimeType,
					Size = part.Content?.Stream?.Length ?? 0,
					ContentId = part.ContentId,
					IsInline = isInline,
				}
			);
		}
	}
	private sealed record ContentFetchSnapshot(
		Guid OccurrenceId,
		Guid MailboxId,
		string ProviderOccurrenceId,
		int TopologyGeneration
	);

}
