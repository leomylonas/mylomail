using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Attachments.CreateUploadSession;
using Microsoft.Graph.Models;
using MyloMail.Api.Domain;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Sending (§1, §15). Always draft-then-send, never the direct <c>/sendMail</c> shortcut:
/// creating a draft first and sending by its immutable id is what makes post-crash
/// reconciliation deterministic — a message dispatched via <c>/sendMail</c> returns no id at
/// all, leaving nothing but the <c>internetMessageId</c> heuristic to reconcile against, which
/// is exactly the gap the doc's own reconciliation story says this path exists to close (§15).
/// Attachments above Graph's ~3MB inline limit additionally need the persisted draft's id to
/// upload chunks against, but that is a second reason to draft first, not the only one.
/// </summary>
public sealed partial class GraphMailProvider
{
	private const long InlineAttachmentLimit = 3 * 1024 * 1024;

	/// <summary>Above this, a slice is uploaded per Graph's own chunking requirement (a
	/// multiple of 320 KiB).</summary>
	private const int UploadChunkSize = 320 * 1024 * 12; // ~3.75MB

	public async Task SendAsync(
		Account account,
		Draft draft,
		string stableMessageId,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var small = draft.Attachments.Where(a => a.Content.LongLength <= InlineAttachmentLimit).ToList();
		var large = draft.Attachments.Where(a => a.Content.LongLength > InlineAttachmentLimit).ToList();

		var message = ToOutgoingMessage(draft, stableMessageId);
		message.Attachments = [.. small.Select(ToFileAttachment)];

		var created =
			await client.Me.Messages.PostAsync(message, cancellationToken: ct)
			?? throw new InvalidOperationException("Graph did not return the created draft.");
		var draftId = created.Id ?? throw new InvalidOperationException("Graph's created draft has no id.");

		foreach (var attachment in large)
		{
			await UploadLargeAttachmentAsync(client, draftId, attachment, ct);
		}

		await client.Me.Messages[draftId].Send.PostAsync(cancellationToken: ct);
	}

	/// <summary>
	/// Uploads one attachment above the inline limit in fixed-size slices against a Graph
	/// upload session, per Graph's own large-file-attachment protocol.
	/// </summary>
	private static async Task UploadLargeAttachmentAsync(
		GraphServiceClient client,
		string draftId,
		DraftAttachment attachment,
		CancellationToken ct
	)
	{
		var session = await client.Me.Messages[draftId].Attachments.CreateUploadSession.PostAsync(
			new CreateUploadSessionPostRequestBody
			{
				AttachmentItem = new AttachmentItem
				{
					AttachmentType = AttachmentType.File,
					Name = attachment.Filename,
					Size = attachment.Content.LongLength,
					ContentType = attachment.MimeType,
				},
			},
			cancellationToken: ct
		);
		var uploadUrl =
			session?.UploadUrl ?? throw new InvalidOperationException("Graph did not return an upload session URL.");

		using var http = new HttpClient();
		var total = attachment.Content.LongLength;
		for (long offset = 0; offset < total; offset += UploadChunkSize)
		{
			var length = (int)Math.Min(UploadChunkSize, total - offset);
			using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
			{
				Content = new ByteArrayContent(attachment.Content, (int)offset, length),
			};
			request.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(
				offset,
				offset + length - 1,
				total
			);
			request.Content.Headers.ContentLength = length;
			var response = await http.SendAsync(request, ct);
			response.EnsureSuccessStatusCode();
		}
	}

	private static GraphMessage ToOutgoingMessage(Draft draft, string stableMessageId) =>
		new()
		{
			// Best-effort: Graph generates its own internetMessageId server-side for a message
			// sent via /sendMail, and whether it honours a caller-supplied one has not been
			// confirmed against a live account (§1) — reconciliation's Message-ID search may
			// therefore need the fallback heuristics it already has for exactly this case.
			InternetMessageId = stableMessageId,
			ToRecipients = [.. draft.To.Select(ToRecipient)],
			CcRecipients = [.. draft.Cc.Select(ToRecipient)],
			BccRecipients = [.. draft.Bcc.Select(ToRecipient)],
			Subject = draft.Subject,
			Body = new ItemBody { ContentType = BodyType.Html, Content = draft.BodyHtml },
			InternetMessageHeaders =
				draft.InReplyToHeader is string inReplyTo
					?
					[
						new InternetMessageHeader { Name = "In-Reply-To", Value = inReplyTo },
						new InternetMessageHeader { Name = "References", Value = inReplyTo },
					]
					: null,
		};

	private static Recipient ToRecipient(Address address) =>
		new() { EmailAddress = new EmailAddress { Name = address.Name, Address = address.Email } };

	private static FileAttachment ToFileAttachment(DraftAttachment attachment) =>
		new()
		{
			Name = attachment.Filename,
			ContentType = attachment.MimeType,
			ContentBytes = attachment.Content,
			IsInline = attachment.IsInline,
			ContentId = attachment.ContentId,
		};
}
