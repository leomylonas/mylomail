using System.Reflection;
using System.Text.RegularExpressions;

namespace MyloMail.Api.Content;

/// <summary>
/// Wraps a prepared message body in the document the reading pane's iframe loads, and owns the
/// presentation rules that go with it: the default typeface, dark mode and print.
/// </summary>
public static partial class MessageFrameDocument
{
	/// <summary>The inverse (<c>255 - c</c>) of Carbon g100's <c>--cds-background</c>, #161616.</summary>
	private const string DarkCanvasInverse = "#e9e9e9";

	private const string PrintStyle =
		"@media print { html, body { margin: 0 !important; background: #fff !important; color: #000 !important; } img { max-width: 100% !important; height: auto !important; } }";

	/// <summary>
	/// The document for <paramref name="fragment"/>. The app's own rules follow the message's, so a
	/// message cannot out-rank the dark-mode or print rules.
	/// </summary>
	public static string Build(string fragment, bool dark, bool invertColours) =>
		$"<!doctype html><html><head><meta charset=\"utf-8\"></head><body>{fragment}<style>{DefaultFontCss.Value}{DarkModeStyle(fragment, dark, invertColours)}{PrintStyle}</style></body></html>";

	/// <summary>Whether the message sets any background of its own — one it was designed against and is shown as authored.</summary>
	public static bool DeclaresBackground(string html) => AuthoredBackground().IsMatch(html);

	/// <summary>
	/// On-screen CSS that keeps a message with no background of its own from glaring white in a dark
	/// theme, or an empty string when nothing needs doing. <paramref name="forceInvert"/> is the
	/// reader asking for it explicitly.
	/// </summary>
	/// <remarks>
	/// The whole document is inverted and hue-rotated back, so the text colours the author chose for a
	/// white page flip to something readable instead of sitting unreadably on a dark canvas, and images,
	/// video and SVG are inverted a second time to restore them. A message that declares its own
	/// background is left alone: inverting it would mangle a design that was made for that colour.
	/// </remarks>
	public static string DarkModeStyle(string html, bool dark, bool forceInvert)
	{
		if (!forceInvert && (!dark || DeclaresBackground(html)))
		{
			return string.Empty;
		}

		const string restore = "invert(1) hue-rotate(180deg)";
		return $"@media screen {{ html {{ background: {DarkCanvasInverse}; filter: {restore}; }} img, picture, video, canvas, svg, iframe {{ filter: {restore}; }} }}";
	}

	// `: initial` is what removing a shorthand's remote URL leaves behind: no background at all.
	[GeneratedRegex(@"\bbgcolor\s*=|\bbackground\s*=|\bbackground(?:-color|-image)?\s*:(?!\s*initial)", RegexOptions.IgnoreCase)]
	private static partial Regex AuthoredBackground();

	/// <summary>
	/// What a message with no font of its own is shown in: the app's own typeface, so it reads as part
	/// of the interface rather than the browser's default serif. Embedded as data URLs because the
	/// document's policy lets fonts load from <c>data:</c> only. Written with no specificity, so
	/// anything the message declares overrides it.
	/// </summary>
	private static readonly Lazy<string> DefaultFontCss = new(() =>
	{
		static string Face(string file, string style, int weight) =>
			$"@font-face {{ font-family: \"IBM Plex Sans\"; font-style: {style}; font-weight: {weight}; src: url(\"data:font/woff2;base64,{Convert.ToBase64String(Resource(file))}\") format(\"woff2\"); }}";

		return string.Join(
			'\n',
			Face("IBMPlexSans-Regular.woff2", "normal", 400),
			Face("IBMPlexSans-Italic.woff2", "italic", 400),
			Face("IBMPlexSans-Bold.woff2", "normal", 700),
			// :where() has no specificity, so any rule the message writes for body wins whatever its order.
			":where(body) { font-family: \"IBM Plex Sans\", system-ui, \"Segoe UI\", Roboto, \"Helvetica Neue\", Arial, sans-serif; }"
		);
	});

	private static byte[] Resource(string file)
	{
		var assembly = typeof(MessageFrameDocument).Assembly;
		var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith(file, StringComparison.Ordinal));
		using var stream = assembly.GetManifestResourceStream(name)!;
		using var buffer = new MemoryStream();
		stream.CopyTo(buffer);
		return buffer.ToArray();
	}
}
