using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Content;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Controllers;

/// <summary>
/// Prepares a message body for display and serves it to the reading pane's iframe (§13).
/// </summary>
/// <remarks>
/// <para>
/// Preparation happens here, in the process that holds the mail, rather than in the renderer that
/// also holds the capability to mutate it: the renderer is handed a document and a count, never
/// untrusted markup to clean. <see cref="MessageHtmlPreparer"/> removes what should never run and
/// withholds remote content; the <see cref="Policy"/> the document is served under is the second,
/// independent defence, enforced by the browser at the request layer.
/// </para>
/// <para>
/// The policy is set here, never by the renderer or the message: a message may ask for fewer
/// permissions through its own markup (policies only intersect) but never for more. A frame loaded
/// from a URL gets only the policy its response carries, unlike a <c>srcdoc</c> frame, which
/// inherits the app page's and so cannot show inline styles.
/// </para>
/// </remarks>
[ApiController]
public sealed class MessageHtmlController(MyloMailDbContext context) : ControllerBase
{
	/// <summary>What the pane needs to know about the prepared message: how much was withheld, and why.</summary>
	[HttpGet("messages/{messageId:guid}/html-info")]
	public async Task<ActionResult<MessageHtmlInfoDto>> Info(Guid messageId, [FromQuery] bool remote, CancellationToken ct)
	{
		var prepared = await PrepareAsync(messageId, remote, quote: false, ct);
		if (prepared is null)
		{
			return (ActionResult<MessageHtmlInfoDto>)this.MutationProblem("This message has no HTML content.", statusCode: StatusCodes.Status404NotFound);
		}

		Response.Headers.CacheControl = "no-store";
		return new MessageHtmlInfoDto(prepared.BlockedRemoteCount, prepared.Decision, prepared.RemoteAllowed);
	}

	/// <summary>
	/// The message prepared for quoting in a reply or forward: remote content stays withheld whatever the
	/// rules say, and inline images keep their <c>cid:</c> references, because the new draft re-attaches them.
	/// </summary>
	[HttpGet("messages/{messageId:guid}/quote-html")]
	public async Task<ActionResult<QuotedHtmlDto>> Quote(Guid messageId, CancellationToken ct)
	{
		var prepared = await PrepareAsync(messageId, readerAskedForRemote: false, quote: true, ct);
		if (prepared is null)
		{
			return (ActionResult<QuotedHtmlDto>)this.MutationProblem("This message has no HTML content.", statusCode: StatusCodes.Status404NotFound);
		}

		Response.Headers.CacheControl = "no-store";
		return new QuotedHtmlDto(prepared.Html);
	}

	/// <summary>The message prepared for the print layout, with its styles separated from the markup.</summary>
	[HttpGet("messages/{messageId:guid}/print-html")]
	public async Task<ActionResult<PrintHtmlDto>> Print(Guid messageId, [FromQuery] bool remote, CancellationToken ct)
	{
		var prepared = await PrepareAsync(messageId, remote, quote: false, ct);
		if (prepared is null)
		{
			return (ActionResult<PrintHtmlDto>)this.MutationProblem("This message has no HTML content.", statusCode: StatusCodes.Status404NotFound);
		}

		var separated = MessageHtmlPreparer.SeparateStyles(prepared.Html);
		Response.Headers.CacheControl = "no-store";
		return new PrintHtmlDto(separated.Html, separated.Sheets, separated.Inline);
	}

	/// <summary>The document the iframe loads, under its own content policy.</summary>
	[HttpGet("messages/{messageId:guid}/frame")]
	public async Task<IActionResult> Frame(
		Guid messageId,
		[FromQuery] bool remote,
		[FromQuery] bool dark,
		[FromQuery] bool invert,
		CancellationToken ct
	)
	{
		var prepared = await PrepareAsync(messageId, remote, quote: false, ct);
		if (prepared is null)
		{
			return NotFound();
		}

		Response.Headers.ContentSecurityPolicy = Policy(prepared.RemoteAllowed);
		Response.Headers["X-Content-Type-Options"] = "nosniff";
		Response.Headers["Referrer-Policy"] = "no-referrer";
		Response.Headers.CacheControl = "no-store";
		return Content(MessageFrameDocument.Build(prepared.Html, dark, invert), "text/html; charset=utf-8");
	}

	private sealed record Prepared(string Html, int BlockedRemoteCount, RemoteContentDecision Decision, bool RemoteAllowed);

	private async Task<Prepared?> PrepareAsync(Guid messageId, bool readerAskedForRemote, bool quote, CancellationToken ct)
	{
		var html = await context
			.MessageBodies.AsNoTracking()
			.Where(body => body.MessageId == messageId)
			.Select(body => body.HtmlBody)
			.FirstOrDefaultAsync(ct);
		if (string.IsNullOrWhiteSpace(html))
		{
			return null;
		}

		var sender = (
			await context.Messages.AsNoTracking().Where(m => m.Id == messageId).Select(m => m.From).FirstOrDefaultAsync(ct)
		)?.FirstOrDefault()?.Email;
		var rules = await context.RemoteContentRules.AsNoTracking().ToListAsync(ct);
		var decision = RemoteContentDecider.Decide(rules, sender);
		var allowed = !quote && RemoteContentDecider.Allows(decision, readerAskedForRemote);

		// The raw message is only read when the body names an inline part.
		InlineImageResolver? resolver = !quote && html.Contains("cid:", StringComparison.OrdinalIgnoreCase)
			? new InlineImageResolver(await LoadMimeAsync(messageId, ct))
			: null;
		var result = MessageHtmlPreparer.Prepare(html, allowed, resolver is null ? null : resolver.Resolve, preserveInline: quote);
		return new Prepared(result.Html, result.BlockedRemoteCount, decision, allowed);
	}

	private async Task<MimeMessage?> LoadMimeAsync(Guid messageId, CancellationToken ct)
	{
		var raw = await context.MessageRaws.AsNoTracking().FirstOrDefaultAsync(r => r.MessageId == messageId, ct);
		if (raw is null)
		{
			return null;
		}

		try
		{
			using var stream = new MemoryStream(raw.Content);
			var mime = await MimeMessage.LoadAsync(stream, ct);
			MimeStructureValidator.Validate(mime);
			return mime;
		}
		catch (Exception ex) when (MessageContentUnavailableException.IsMalformedMime(ex))
		{
			return null;
		}
	}

	/// <summary>
	/// The request-layer enforcement §13 requires. Stripping attributes is a parser competing with an
	/// attacker; a content policy is the browser refusing to make the request at all, and it covers
	/// the routes stripping misses — CSS <c>url()</c>, <c>@import</c>, <c>srcset</c>, SVG references.
	/// <c>default-src 'none'</c> means anything not named here simply cannot load.
	/// </summary>
	internal static string Policy(bool allowRemote) =>
		string.Join(
			"; ",
			"default-src 'none'",
			// Inline images are data URLs. Remote images load only once the reader has asked for them.
			$"img-src data:{(allowRemote ? " https: http:" : string.Empty)}",
			// Inline styles are the point: without them most mail renders as bare markup.
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
