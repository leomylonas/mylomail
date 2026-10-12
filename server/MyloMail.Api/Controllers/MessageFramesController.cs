using Microsoft.AspNetCore.Mvc;
using MyloMail.Api.Content;
using MyloMail.Api.Errors;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Serves a message document to the reading pane's iframe under a content policy of its own.
/// </summary>
/// <remarks>
/// The policy is set here, never by the renderer or the message: a message may ask for fewer
/// permissions through its own markup (policies only intersect) but never for more. Scripts are
/// refused outright, forms and base URLs are refused, nothing may frame it but the app, and the
/// <c>sandbox</c> directive keeps a script-less, form-less document even if the URL is opened
/// directly. Inline styles are allowed — that is the point — because without them most mail
/// renders as bare markup. Remote images load only when the user has allowed them for this
/// message, which the renderer states when it submits the document.
/// </remarks>
[ApiController]
public sealed class MessageFramesController(MessageFrameStore store) : ControllerBase
{
	private const int MaxDocumentBytes = 25 * 1024 * 1024;

	[HttpPost("message-frames")]
	[RequestSizeLimit(MaxDocumentBytes)]
	public IActionResult Create([FromBody] MessageFrameRequest request)
	{
		if (string.IsNullOrEmpty(request.Document))
		{
			return this.MutationProblem("A message document is required.", statusCode: StatusCodes.Status400BadRequest);
		}

		return Ok(new { id = store.Add(request.Document, request.AllowRemote) });
	}

	[HttpGet("message-frames/{id}")]
	public IActionResult Get(string id)
	{
		if (store.Get(id) is not var (document, allowRemote))
		{
			return NotFound();
		}

		Response.Headers.ContentSecurityPolicy = Policy(allowRemote);
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		Response.Headers["Referrer-Policy"] = "no-referrer";
		Response.Headers.CacheControl = "no-store";
		return Content(document, "text/html; charset=utf-8");
	}

	internal static string Policy(bool allowRemote) =>
		string.Join(
			"; ",
			"default-src 'none'",
			// Inline images arrive as blobs, fetched by authenticated renderer code. Remote images
			// load only once the user has asked for them.
			$"img-src blob: data:{(allowRemote ? " https: http:" : string.Empty)}",
			"style-src 'unsafe-inline'",
			"font-src data:",
			// No script, ever. A message is not code.
			"script-src 'none'",
			"form-action 'none'",
			"base-uri 'none'",
			"frame-src 'none'",
			"frame-ancestors 'self'",
			"sandbox allow-same-origin"
		);
}

public sealed record MessageFrameRequest(string Document, bool AllowRemote);
