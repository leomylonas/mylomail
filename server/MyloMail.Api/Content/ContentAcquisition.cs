using Microsoft.EntityFrameworkCore;
using MimeKit;
using MimeKit.Text;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Content;

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

	/// <summary>Fetches and stores one message's content.</summary>
	public async Task AcquireAsync(Account account, Guid messageId, CancellationToken ct = default)
	{
		var state = await context.MessageContentStates.FirstOrDefaultAsync(c => c.MessageId == messageId, ct);
		if (state is null)
		{
			state = new MessageContentState { MessageId = messageId, Status = ContentStatus.NotFetched };
			context.MessageContentStates.Add(state);
		}

		var occurrence = await context.MessageMailboxes.FirstOrDefaultAsync(o => o.MessageId == messageId, ct);
		if (occurrence is null)
		{
			// No membership means nothing addressable on the server. A message can legitimately
			// be in this state for a moment during a move (§3), so it is left alone rather
			// than failed.
			return;
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
					new MessageOccurrenceRef(messageId, occurrence.MailboxId, occurrence.ProviderOccurrenceId),
					ct
				);
		}
		catch (Exception ex)
		{
			// Requeued rather than failed outright while attempts remain: a transient network
			// blip mid-fetch must not read the same as permanently malformed data. `Attempts`
			// was already incremented above the fetch, so this is attempt-counted correctly.
			state.Status = state.Attempts < MaxAttempts ? ContentStatus.Queued : ContentStatus.Failed;
			state.LastError = ex.Message;
			await context.SaveChangesAsync(ct);
			logger.LogWarning(ex, "Content fetch failed for message {MessageId}.", messageId);
			throw;
		}

		try
		{
			await StoreAsync(state, messageId, raw.RawBytes, ct);
		}
		catch (Exception ex)
		{
			// Parsing or storage failed, which will fail again on the same bytes. Left in
			// Fetching it would be picked up forever — the first version of this looped
			// thousands of times on one malformed write, logging a warning each pass.
			await MarkFailedAsync(state, ex, ct);
			throw;
		}
	}

	private async Task MarkFailedAsync(MessageContentState state, Exception ex, CancellationToken ct)
	{
		// A fresh context state: the failed transaction may have left the tracked entities
		// inconsistent with the database.
		context.ChangeTracker.Clear();

		// See the fetch-failure catch above: retried while attempts remain, permanently
		// failed only once they run out.
		var status = state.Attempts < MaxAttempts ? ContentStatus.Queued : ContentStatus.Failed;
		await context
			.MessageContentStates.Where(c => c.MessageId == state.MessageId)
			.ExecuteUpdateAsync(
				updates =>
					updates.SetProperty(c => c.Status, status).SetProperty(c => c.LastError, ex.Message),
				ct
			);
	}

	/// <summary>
	/// Stores the raw bytes and everything parsed from them, in one transaction.
	/// </summary>
	/// <remarks>
	/// The search row is written here rather than by a trigger, in the same transaction as the
	/// content it describes: a committed body with no index entry is invisible to search
	/// forever, and an index entry with no body is a hit pointing at nothing (§8).
	/// </remarks>
	private async Task StoreAsync(
		MessageContentState state,
		Guid messageId,
		byte[] rawBytes,
		CancellationToken ct
	)
	{
		using var stream = new MemoryStream(rawBytes);
		var mime = await MimeMessage.LoadAsync(stream, ct);

		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

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
			message.HasNonInlineAttachments = mime.Attachments.Any();

			state.Status = ContentStatus.Indexed;
			state.LastError = null;

			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});
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
}
