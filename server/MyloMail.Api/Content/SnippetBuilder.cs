using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MyloMail.Api.Content;

/// <summary>
/// The one-line preview a message shows in the list, built from its body.
/// </summary>
/// <remarks>
/// Stripping tags from HTML is not enough: a newsletter's <c>&lt;style&gt;</c> block, its
/// conditional comments and its hidden filler would all become "text" and the preview would read
/// like source code. This reads the document the way a person would: no head, no style or script,
/// no comments, entities decoded, invisible characters removed, and hidden pre-header text (the
/// preview a sender writes on purpose) preferred when it says something.
/// </remarks>
internal static partial class SnippetBuilder
{
	public const int MaxLength = 200;

	/// <summary>Below this many characters of real text a candidate is not a preview.</summary>
	private const int Meaningful = 12;

	public static string Build(string? text, string? html)
	{
		var fromText = Clean(text is null ? null : StripPlainTextNoise(text));
		if (IsMeaningful(fromText))
		{
			return Truncate(fromText);
		}

		if (!string.IsNullOrWhiteSpace(html))
		{
			var (visible, hidden) = ReadHtml(html);
			var preheader = Clean(hidden);
			if (IsMeaningful(preheader))
			{
				return Truncate(preheader);
			}

			var body = Clean(visible);
			if (body.Length > 0)
			{
				return Truncate(body);
			}
		}

		return Truncate(fromText);
	}

	private static bool IsMeaningful(string value) => value.Count(char.IsLetterOrDigit) >= Meaningful;

	private static (string Visible, string Hidden) ReadHtml(string html)
	{
		var document = new HtmlDocument { OptionFixNestedTags = true };
		document.LoadHtml(html);
		var visible = new StringBuilder();
		var hidden = new StringBuilder();
		Walk(document.DocumentNode, visible, hidden, insideHidden: false);
		return (visible.ToString(), hidden.ToString());
	}

	private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
	{
		"head", "style", "script", "title", "noscript", "template", "meta", "link", "svg", "xml",
	};

	private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
	{
		"p", "div", "br", "tr", "td", "th", "li", "ul", "ol", "table", "h1", "h2", "h3", "h4", "h5", "h6",
		"blockquote", "section", "article", "header", "footer", "hr",
	};

	private static void Walk(HtmlNode node, StringBuilder visible, StringBuilder hidden, bool insideHidden)
	{
		switch (node.NodeType)
		{
			case HtmlNodeType.Comment:
				return;
			case HtmlNodeType.Text:
				(insideHidden ? hidden : visible).Append(' ').Append(WebUtility.HtmlDecode(((HtmlTextNode)node).Text));
				return;
		}

		if (Skipped.Contains(node.Name))
		{
			return;
		}

		var nowHidden = insideHidden || IsHidden(node);
		var target = nowHidden ? hidden : visible;
		if (Blocks.Contains(node.Name))
		{
			target.Append(' ');
		}

		foreach (var child in node.ChildNodes)
		{
			Walk(child, visible, hidden, nowHidden);
		}

		if (Blocks.Contains(node.Name))
		{
			target.Append(' ');
		}
	}

	private static bool IsHidden(HtmlNode node)
	{
		if (node.Attributes["hidden"] is not null)
		{
			return true;
		}

		var style = node.GetAttributeValue("style", string.Empty);
		return style.Length > 0 && HiddenStyle().IsMatch(style);
	}

	[GeneratedRegex(
		@"display\s*:\s*none|visibility\s*:\s*hidden|mso-hide\s*:\s*all|opacity\s*:\s*0(?![.\d])|(?:max-)?height\s*:\s*0(?:px)?\s*(?:;|$|!)|font-size\s*:\s*0(?:px)?\s*(?:;|$|!)|(?:max-)?width\s*:\s*0(?:px)?\s*(?:;|$|!)",
		RegexOptions.IgnoreCase
	)]
	private static partial Regex HiddenStyle();

	/// <summary>Link markers and inline-image placeholders that plain-text parts carry.</summary>
	private static string StripPlainTextNoise(string text) =>
		Url().Replace(
			Placeholder().Replace(text, " "),
			match => Uri.TryCreate(match.Value.Trim('<', '>'), UriKind.Absolute, out var uri) && match.Length > 40
				? uri.Host
				: match.Value
		);

	[GeneratedRegex(@"\[(?:image|cid|inline)[^\]]*\]", RegexOptions.IgnoreCase)]
	private static partial Regex Placeholder();

	[GeneratedRegex(@"<?https?://[^\s>]+>?", RegexOptions.IgnoreCase)]
	private static partial Regex Url();

	/// <summary>
	/// Removes the characters senders use to pad a preview (zero-width and soft-hyphen filler, the
	/// combining grapheme joiner, non-breaking spaces) and collapses whitespace.
	/// </summary>
	private static string Clean(string? value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return string.Empty;
		}

		var builder = new StringBuilder(value.Length);
		foreach (var ch in value)
		{
			if (IsInvisible(ch))
			{
				continue;
			}

			builder.Append(ch is '\u00A0' or '\u2007' or '\u202F' ? ' ' : ch);
		}

		return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
	}

	private static bool IsInvisible(char ch) =>
		ch is '\u00AD' or '\u034F' or '\u061C' or '\u180E' or '\uFEFF'
		|| ch is >= '\u200B' and <= '\u200F'
		|| ch is >= '\u202A' and <= '\u202E'
		|| ch is >= '\u2060' and <= '\u206F';

	private static string Truncate(string collapsed)
	{
		if (collapsed.Length <= MaxLength)
		{
			return collapsed;
		}

		// A raw code-unit slice can land inside a surrogate pair (an emoji, say); back off one
		// character rather than leave an unpaired low surrogate that renders as a replacement
		// character or corrupts the string downstream.
		var cut = MaxLength;
		if (char.IsHighSurrogate(collapsed[cut - 1]))
		{
			cut--;
		}

		return collapsed[..cut];
	}
}
