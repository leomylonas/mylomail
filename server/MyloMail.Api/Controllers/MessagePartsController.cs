using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Serves one MIME part of a stored message (§13, Epic 5).
/// </summary>
/// <remarks>
/// <para>
/// Inline images are fetched here and turned into <c>blob:</c> URLs by the renderer, rather
/// than left as <c>cid:</c> for the browser to resolve. An <c>img src</c> request carries no
/// bearer token, and the launch token must never appear in a URL — so the only way to fetch
/// one authenticated is through code that can send credentials, which is this endpoint plus
/// the same-origin launch cookie.
/// </para>
/// <para>
/// Parts are extracted from the stored raw message rather than from a decoded copy, because
/// raw MIME is the single stored representation (§1).
/// </para>
/// </remarks>
[ApiController]
[Route("messages/{messageId:guid}/parts")]
public class MessagePartsController(MyloMailDbContext context) : ControllerBase
{
	/// <summary>Returns the part with the given <c>Content-ID</c>.</summary>
	[HttpGet("{contentId}")]
	public async Task<IActionResult> Get(Guid messageId, string contentId, CancellationToken ct)
	{
		var raw = await context.MessageRaws.FirstOrDefaultAsync(r => r.MessageId == messageId, ct);
		if (raw is null)
		{
			return NotFound();
		}

		using var stream = new MemoryStream(raw.Content);
		var mime = await MimeMessage.LoadAsync(stream, ct);

		var wanted = contentId.Trim('<', '>');
		var part = mime
			.BodyParts.OfType<MimePart>()
			.FirstOrDefault(candidate => candidate.ContentId?.Trim('<', '>') == wanted);

		if (part?.Content is null)
		{
			return NotFound();
		}

		var decoded = new MemoryStream();
		await part.Content.DecodeToAsync(decoded, ct);
		decoded.Position = 0;

		// The declared type, not a sniffed one: this is remote-authored content, and letting
		// the browser decide what it is invites a part declared as an image being treated as
		// something executable.
		return File(decoded, part.ContentType.MimeType);
	}

}
