using Microsoft.AspNetCore.Mvc;
using MyloMail.Api.Compose;
using MyloMail.Api.Content;

namespace MyloMail.Api.Controllers;

/// <summary>Stages files inside the structured local draft until MIME is generated at send.</summary>
[ApiController]
[Route("drafts/{draftId:guid}/attachments")]
public sealed class DraftAttachmentsController(DraftService drafts) : ControllerBase
{
	[HttpPost]
	[RequestSizeLimit(160 * 1024 * 1024)]
	public async Task<IActionResult> Upload(Guid draftId, CancellationToken ct)
	{
		var file = Request.Form.Files.GetFile("file");
		if (file is null)
		{
			return BadRequest("An attachment file is required.");
		}

		if (file.Length == 0)
		{
			return BadRequest("An empty attachment cannot be uploaded.");
		}

		await using var content = new MemoryStream();
		await file.CopyToAsync(content, ct);
		// isInline/contentId are opt-in form fields, not part of a dropped/browsed file's own
		// shape - only the reply/forward copy path (§13) ever sends them, to preserve an inline
		// image's cid: binding onto the new draft.
		var isInline = Request.Form.TryGetValue("isInline", out var isInlineValue)
			&& bool.TryParse(isInlineValue, out var parsedIsInline)
			&& parsedIsInline;
		var contentId = Request.Form.TryGetValue("contentId", out var contentIdValue)
			&& !string.IsNullOrWhiteSpace(contentIdValue)
			? contentIdValue.ToString()
			: null;
		var attachment = await drafts.AddAttachmentAsync(
			draftId,
			AttachmentTempDirectory.SanitiseFilename(file.FileName),
			string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
			content.ToArray(),
			ct,
			isInline,
			contentId
		);
		return Ok(new { attachment.Id, attachment.Filename, attachment.MimeType, attachment.Size });
	}

	[HttpDelete("{attachmentId:guid}")]
	public async Task<IActionResult> Remove(Guid draftId, Guid attachmentId, CancellationToken ct)
	{
		await drafts.RemoveAttachmentAsync(draftId, attachmentId, ct);
		return NoContent();
	}
}
