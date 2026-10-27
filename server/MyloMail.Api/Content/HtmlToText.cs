using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MyloMail.Api.Content;

/// <summary>HTML markup as the plain text a text box can show and edit: paragraphs and breaks kept as line breaks, tags, head and style dropped.</summary>
public static partial class HtmlToText
{
	private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
	{
		"head", "style", "script", "title", "template", "noscript",
	};

	private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
	{
		"p", "div", "tr", "li", "ul", "ol", "table", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "section", "article", "hr",
	};

	public static string Convert(string html)
	{
		var document = new HtmlDocument { OptionFixNestedTags = true };
		document.LoadHtml(html);
		var text = new StringBuilder();
		Walk(document.DocumentNode, text);

		var lines = text
			.ToString()
			.Replace('\u00A0', ' ')
			.Split('\n')
			.Select(line => Spaces().Replace(line, " ").Trim());
		return Blank().Replace(string.Join('\n', lines), "\n\n").Trim();
	}

	private static void Walk(HtmlNode node, StringBuilder text)
	{
		switch (node.NodeType)
		{
			case HtmlNodeType.Comment:
				return;
			case HtmlNodeType.Text:
				text.Append(WebUtility.HtmlDecode(((HtmlTextNode)node).Text).Replace("\r", string.Empty).Replace('\n', ' '));
				return;
		}

		if (Skipped.Contains(node.Name))
		{
			return;
		}

		if (node.Name.Equals("br", StringComparison.OrdinalIgnoreCase))
		{
			text.Append('\n');
			return;
		}

		var block = Blocks.Contains(node.Name);
		if (block)
		{
			text.Append('\n');
		}

		foreach (var child in node.ChildNodes)
		{
			Walk(child, text);
		}

		if (block)
		{
			text.Append('\n');
		}
	}

	[GeneratedRegex(@"[ \t]+")]
	private static partial Regex Spaces();

	[GeneratedRegex(@"\n{3,}")]
	private static partial Regex Blank();
}
