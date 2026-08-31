using Microsoft.AspNetCore.Mvc;
using MyloMail.Api.Content;

namespace MyloMail.Api.Controllers;

/// <summary>Downloads or prepares received MIME attachments without duplicating their storage.</summary>
[ApiController]
[Route("messages/{messageId:guid}/attachments")]
public sealed class MessageAttachmentsController(AttachmentService attachments) : ControllerBase
{
	[HttpGet("{attachmentId:guid}")]
	public async Task<IActionResult> Download(Guid messageId, Guid attachmentId, CancellationToken ct)
	{
		try
		{
			var (attachment, content) = await attachments.ReadAsync(messageId, attachmentId, ct);
			return File(content, attachment.MimeType, AttachmentTempDirectory.SanitiseFilename(attachment.Filename));
		}
		catch (FileNotFoundException)
		{
			return NotFound();
		}
	}

	[HttpPost("{attachmentId:guid}/open")]
	public async Task<IActionResult> PrepareForOpen(Guid messageId, Guid attachmentId, CancellationToken ct)
	{
		try
		{
			var path = await attachments.MaterialiseForOpeningAsync(messageId, attachmentId, ct);
			return Ok(new { path });
		}
		catch (FileNotFoundException)
		{
			return NotFound();
		}
	}
}
