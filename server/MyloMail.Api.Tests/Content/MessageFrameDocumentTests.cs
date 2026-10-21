using MyloMail.Api.Content;
using Xunit;

namespace MyloMail.Api.Tests.Content;

public sealed class MessageFrameDocumentTests
{
	[Theory]
	[InlineData("<table bgcolor=\"#ffffff\"><tr><td>x</td></tr></table>")]
	[InlineData("<body background=\"bg.png\">x</body>")]
	[InlineData("<div style=\"background-color: #fff\">x</div>")]
	[InlineData("<style>td { background:#eee }</style><td>x</td>")]
	[InlineData("<div style=\"BACKGROUND-IMAGE:url(x)\">x</div>")]
	public void An_authored_background_is_detected(string html) =>
		Assert.True(MessageFrameDocument.DeclaresBackground(html));

	[Theory]
	[InlineData("<p style=\"color:#333; border: 1px solid\">Hi</p>")]
	[InlineData("<p>The background of this story</p>")]
	// What removing a shorthand's remote URL leaves behind is no background at all.
	[InlineData("<div style=\"color: red; background-color: initial\">x</div>")]
	public void Text_colour_other_properties_and_an_emptied_background_are_not_a_background(string html) =>
		Assert.False(MessageFrameDocument.DeclaresBackground(html));

	[Fact]
	public void Dark_mode_does_nothing_in_a_light_theme() =>
		Assert.Equal(string.Empty, MessageFrameDocument.DarkModeStyle("<p>Hi</p>", dark: false, forceInvert: false));

	[Fact]
	public void Dark_mode_does_nothing_to_a_message_that_brings_its_own_background() =>
		Assert.Equal(string.Empty, MessageFrameDocument.DarkModeStyle("<td bgcolor=\"#fff\">Hi</td>", dark: true, forceInvert: false));

	[Fact]
	public void An_unstyled_message_is_inverted_on_screen_only_and_its_images_restored()
	{
		var style = MessageFrameDocument.DarkModeStyle("<p>Hi</p>", dark: true, forceInvert: false);

		Assert.Contains("@media screen", style);
		Assert.Contains("html {", style);
		Assert.DoesNotContain("<style", style);
		Assert.Matches(@"img[^}]*filter: invert\(1\) hue-rotate\(180deg\)", style);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void The_reader_can_invert_a_message_with_its_own_background_in_any_theme(bool dark) =>
		Assert.Contains("invert(1)", MessageFrameDocument.DarkModeStyle("<td bgcolor=\"#fff\">Hi</td>", dark, forceInvert: true));

	[Fact]
	public void The_document_puts_the_apps_rules_after_the_messages_and_gives_unstyled_text_the_apps_typeface()
	{
		var document = MessageFrameDocument.Build("<style>p{color:red}</style><p>Hi</p>", dark: true, invertColours: false);

		Assert.StartsWith("<!doctype html>", document);
		Assert.True(document.IndexOf("p{color:red}", StringComparison.Ordinal) < document.IndexOf("@media screen", StringComparison.Ordinal));
		Assert.Contains("@layer messageDefaults", document);
		Assert.Contains(".messageContent {", document);
		Assert.Contains("<body class=\"messageContent\">", document);
		Assert.Contains("font-family:", document);
		Assert.Contains("data:font/woff2;base64,", document);
		Assert.Contains("@media print", document);
	}
}
