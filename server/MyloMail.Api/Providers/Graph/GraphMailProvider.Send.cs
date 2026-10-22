using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Attachments.CreateUploadSession;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
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
		var draftId = draft.ProviderDraftId
			?? throw new InvalidOperationException(
				"Microsoft Graph send requires a durably persisted server-draft identity."
			);
		if (!string.Equals(draft.StableMessageId, stableMessageId, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"The persisted Microsoft Graph draft does not match this send attempt."
			);
		}

		// Draft creation/update and its immutable id are owned by DraftSyncService and commit
		// before SendExecutor marks the externally visible send attempt Dispatched. This call
		// is therefore one irreversible provider boundary, not create+upload+send hidden
		// behind one ambiguous attempt.
		await ThrottleAwareAsync(() => client.Me.Messages[draftId].Send.PostAsync(cancellationToken: ct));
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
		var session = await ThrottleAwareAsync(
			() => client.Me.Messages[draftId].Attachments.CreateUploadSession.PostAsync(
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
			)
		);
		var uploadUrl =
			session?.UploadUrl ?? throw new InvalidOperationException("Graph did not return an upload session URL.");
		await UploadLargeAttachmentContentAsync(client, uploadUrl, attachment, ct);
	}

	internal static async Task UploadLargeAttachmentContentAsync(
		GraphServiceClient client,
		string uploadUrl,
		DraftAttachment attachment,
		CancellationToken ct
	)
	{
		var total = attachment.Content.LongLength;
		for (long offset = 0; offset < total; offset += UploadChunkSize)
		{
			var length = (int)Math.Min(UploadChunkSize, total - offset);
			using var stream = new MemoryStream(
				attachment.Content,
				(int)offset,
				length,
				writable: false
			);
			var request = new RequestInformation
			{
				HttpMethod = Method.PUT,
				URI = new Uri(uploadUrl),
			};
			request.Headers.Add(
				"Content-Range",
				$"bytes {offset}-{offset + length - 1}/{total}"
			);
			request.Headers.Add(
				"Content-Length",
				length.ToString(System.Globalization.CultureInfo.InvariantCulture)
			);
			request.SetStreamContent(stream, "application/octet-stream");
			await ThrottleAwareAsync(
				() => client.RequestAdapter.SendNoContentAsync(request, cancellationToken: ct)
			);
		}
	}


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
