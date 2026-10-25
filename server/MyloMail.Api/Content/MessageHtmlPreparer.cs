using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace MyloMail.Api.Content;

/// <summary>The result of preparing a message body for display.</summary>
/// <param name="Html">A sanitised fragment, safe to place in a document the message does not control.</param>
/// <param name="BlockedRemoteCount">How many remote references were withheld, so the UI can offer to load them.</param>
public sealed record PreparedHtml(string Html, int BlockedRemoteCount);

/// <summary>Sanitised markup with its styles taken out; see <see cref="MessageHtmlPreparer.SeparateStyles"/>.</summary>
public sealed record PrintHtml(string Html, IReadOnlyList<string> Sheets, IReadOnlyDictionary<string, string> Inline);

/// <summary>
/// Sanitises a message body and withholds its remote content (§13).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent defences, on purpose.</b> This strips what should never run; the content
/// policy the document is served under refuses the requests anyway. Sanitisation alone is a parser
/// competing with an attacker who only has to win once, and a policy alone would still leave
/// scripts in the DOM of an isolated document. Neither is sufficient, so both are applied.
/// </para>
/// <para>
/// §13 is explicit that removing <c>&lt;img src&gt;</c> is not enough: <c>srcset</c>, CSS
/// <c>url()</c>, SVG references and <c>@import</c> all issue requests. Remote references in CSS —
/// in a <c>style</c> attribute or a <c>&lt;style&gt;</c> element — are removed as declarations, so
/// the rest of the message's styling survives. <c>cid:</c> references name a part of this message,
/// which is already downloaded, and are replaced with the part itself as a data URL.
/// </para>
/// </remarks>
public static partial class MessageHtmlPreparer
{
	/// <summary>Form controls exist to collect credentials; there is no legitimate use for one in mail.</summary>
	private static readonly string[] ForbiddenTags =
	[
		"form", "input", "button", "select", "option", "optgroup", "textarea", "fieldset", "legend",
		"datalist", "output", "keygen", "meter", "progress", "menu", "menuitem",
	];

	/// <summary>
	/// Attributes that fetch something, other than the URL attributes the sanitiser already knows
	/// (<c>src</c>, <c>href</c>, <c>background</c>…): never allowed, and counted when they named a remote resource.
	/// </summary>
	private static readonly string[] FetchingAttributes = ["srcset", "poster", "data-src", "ping"];

	private static readonly string[] RasterImageTypes =
	[
		"image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/webp", "image/bmp", "image/avif",
		"image/x-icon", "image/vnd.microsoft.icon",
	];

	/// <param name="preserveInline">
	/// Leave <c>cid:</c> references as they are rather than resolving them: a reply or forward quotes the
	/// message into an editor, where the inline images are re-attached to the new draft and bound by the
	/// same Content-ID.
	/// </param>
	public static PreparedHtml Prepare(
		string html,
		bool allowRemote,
		Func<string, string?>? resolveInline = null,
		bool preserveInline = false
	)
	{
		var blocked = 0;
		var sanitizer = Configure();

		sanitizer.FilterUrl += (_, e) =>
		{
			var url = e.OriginalUrl.Trim();
			var tag = e.Tag?.NodeName ?? string.Empty;
			var isLink = tag is "A" or "AREA";

			if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
			{
				// A part of this message, already downloaded. Nothing else may use a cid: URL.
				if (preserveInline && !isLink)
				{
					return;
				}

				e.SanitizedUrl = isLink ? null : resolveInline?.Invoke(url[4..]);
				return;
			}

			if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
			{
				// Embedded raster images only. A data: link navigates to attacker-supplied markup,
				// and an SVG can carry more than an image should.
				e.SanitizedUrl = !isLink && IsRasterDataUrl(url) ? url : null;
				return;
			}

			if (url.StartsWith('#'))
			{
				e.SanitizedUrl = isLink ? url : null;
				return;
			}

			var remote = url.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
				|| url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
				|| url.StartsWith("//", StringComparison.Ordinal);
			if (!remote)
			{
				// mailto:, tel: and the like are only ever links; a relative URL would resolve against
				// the backend that serves the document, which no message has any business reaching.
				// The sanitiser has already refused schemes it does not allow (javascript:, vbscript:), so
				// what is left for a link is only its decision to keep.
				if (!isLink || !url.Contains(':'))
				{
					e.SanitizedUrl = null;
				}

				return;
			}

			if (isLink)
			{
				return;
			}

			if (!allowRemote)
			{
				blocked++;
				e.SanitizedUrl = null;
			}
		};

		sanitizer.RemovingAttribute += (_, e) =>
		{
			if (!allowRemote && FetchingAttributes.Contains(e.Attribute.Name, StringComparer.OrdinalIgnoreCase)
				&& (e.Attribute.Value.Contains("http", StringComparison.OrdinalIgnoreCase) || e.Attribute.Value.Contains("//")))
			{
				blocked++;
			}
		};

		return new PreparedHtml(sanitizer.Sanitize(NamespacedTag().Replace(html, string.Empty)), blocked);
	}

	/// <summary>
	/// Takes the style elements and style attributes out of already-sanitised markup. The app's own
	/// page refuses both when markup is placed in it, and reports each one, so for the print layout
	/// they are handed over separately, to be applied through the CSS object model.
	/// </summary>
	public static PrintHtml SeparateStyles(string sanitisedHtml)
	{
		var parser = new HtmlParser();
		var document = parser.ParseDocument(string.Empty);
		var body = document.Body!;
		var nodes = parser.ParseFragment(sanitisedHtml, body);
		var container = document.CreateElement("div");
		foreach (var node in nodes.ToList())
		{
			container.AppendChild(node);
		}

		var sheets = new List<string>();
		foreach (var style in container.QuerySelectorAll("style").ToList())
		{
			sheets.Add(style.TextContent);
			style.Remove();
		}

		var inline = new Dictionary<string, string>();
		foreach (var element in container.QuerySelectorAll("[style]").ToList())
		{
			var id = inline.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
			inline[id] = element.GetAttribute("style") ?? string.Empty;
			element.RemoveAttribute("style");
			element.SetAttribute("data-inline-style", id);
		}

		return new PrintHtml(container.InnerHtml, sheets, inline);
	}

	/// <summary>
	/// A template engine's or Office's own tag (<c>&lt;show:Product&gt;</c>, <c>&lt;o:p&gt;</c>) is not HTML. The
	/// sanitiser drops an unknown element together with everything inside it, which for a tag wrapping
	/// the whole body leaves an empty message; only the tag itself is removed here.
	/// </summary>
	[System.Text.RegularExpressions.GeneratedRegex(@"</?[a-zA-Z][\w-]*:[a-zA-Z][\w:.-]*(?:\s[^>]*)?/?>")]
	private static partial System.Text.RegularExpressions.Regex NamespacedTag();

	private static HtmlSanitizer Configure()
	{
		var sanitizer = new HtmlSanitizer();

		foreach (var tag in ForbiddenTags)
		{
			sanitizer.AllowedTags.Remove(tag);
		}

		// Style elements carry most of a newsletter's layout. The sanitiser parses their CSS, so what
		// survives is declarations it can vouch for, and remote URLs in them go through the same filter
		// as every other URL.
		sanitizer.AllowedTags.Add("style");
		sanitizer.AllowedAtRules.Add(CssRuleType.Media);

		foreach (var attribute in new[] { "class", "id", "face", "background", "role", "lang", "hidden" })
		{
			sanitizer.AllowedAttributes.Add(attribute);
		}

		// A link that opens in a named target, and anything that reaches out on its own, is not for mail.
		sanitizer.AllowedAttributes.Remove("target");
		sanitizer.AllowedAttributes.Remove("action");
		sanitizer.AllowedAttributes.Remove("accept-charset");
		sanitizer.AllowedAttributes.Remove("enctype");
		sanitizer.AllowedAttributes.Remove("method");

		sanitizer.AllowedSchemes.Clear();
		foreach (var scheme in new[] { "http", "https", "mailto", "tel", "cid", "data" })
		{
			sanitizer.AllowedSchemes.Add(scheme);
		}

		sanitizer.AllowDataAttributes = false;
		return sanitizer;
	}

	private static bool IsRasterDataUrl(string url)
	{
		var end = url.IndexOfAny([';', ',']);
		return end > 5 && RasterImageTypes.Contains(url[5..end].Trim().ToLowerInvariant());
	}
}
