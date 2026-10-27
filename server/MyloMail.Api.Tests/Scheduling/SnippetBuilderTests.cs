using MyloMail.Api.Content;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>Previews that read like source code, or like nothing, are what these exist to prevent.</summary>
public sealed class SnippetBuilderTests
{
	[Fact]
	public void A_style_block_and_head_are_never_part_of_the_preview()
	{
		const string html = """
			<html><head><title>x</title><style>@import url('https://fonts.googleapis.com/css2?family=Montserrat'); .a { color: red }</style></head>
			<body><!--[if mso]><table><tr><td><![endif]--><p>Hey there,</p><p>We noticed you recently applied to a position on Jobgether.</p></body></html>
			""";

		Assert.Equal(
			"Hey there, We noticed you recently applied to a position on Jobgether.",
			SnippetBuilder.Build(null, html)
		);
	}

	[Fact]
	public void A_stylesheet_pasted_into_the_plain_text_part_is_not_the_preview()
	{
		// A sender's text alternative that opens with their CSS, followed by the real words.
		const string text = """
			body, table, td { font-family: Arial, Helvetica, sans-serif
			!important; }

			h1, h2, h3 {
			font-family: Arial, Helvetica, sans-serif !important;
			line-height: 96% !important;
			}

			@media (max-width: 639px) {
			.hd { font-size: initial !important; }
			}

			Hey Leo, wanna become a better husband? Become a stripper
			""";

		Assert.Equal("Hey Leo, wanna become a better husband? Become a stripper", SnippetBuilder.Build(text, null));
	}

	[Fact]
	public void Markup_in_the_plain_text_part_is_read_as_html_not_shown_as_tags()
	{
		const string text = "<p style=\"margin: 0; font-weight: 400;\"><span style=\"color: rgb(0, 0, 0);\">Making your next career move can feel overwhelming.</span></p><p><br></p>";

		Assert.Equal("Making your next career move can feel overwhelming.", SnippetBuilder.Build(text, null));
	}

	[Fact]
	public void A_provider_preview_loses_its_padding_and_escapes()
	{
		Assert.Equal(
			"View in browser Ubisoft Hello isn't",
			SnippetBuilder.Normalize("\u200c \u200c \u200c\u200c \u200c View in browser Ubisoft Hello isn&#39;t")
		);
	}

	[Fact]
	public void Ordinary_text_with_braces_is_left_alone()
	{
		Assert.Equal("Use {name} in the template, then {save} it.", SnippetBuilder.Build("Use {name} in the template, then {save} it.", null));
	}

	[Fact]
	public void Entities_are_decoded_and_invisible_filler_is_dropped()
	{
		// A pre-header padded with &#847;&zwnj; to push the real body out of the preview.
		const string html = "<div style=\"display:none\">&#847;&zwnj;&nbsp;&#847;&zwnj;&nbsp;&#847;&zwnj;</div><p>Your online statement is ready &amp; waiting</p>";

		Assert.Equal("Your online statement is ready & waiting", SnippetBuilder.Build(null, html));
	}

	[Fact]
	public void A_hidden_preheader_that_says_something_is_preferred_to_the_body()
	{
		const string html = "<div style=\"display:none;max-height:0\">Your invoice for September is attached</div><table><tr><td>Logo</td><td>Dear customer, thank you for your business over the past year</td></tr></table>";

		Assert.Equal("Your invoice for September is attached", SnippetBuilder.Build(null, html));
	}

	[Fact]
	public void Cells_and_blocks_are_separated_so_words_do_not_run_together()
	{
		Assert.Equal(
			"Order shipped Tracking 12345 arrives Friday",
			SnippetBuilder.Build(null, "<table><tr><td>Order shipped</td><td>Tracking 12345</td></tr></table><p>arrives Friday</p>")
		);
	}

	[Fact]
	public void Link_markers_and_image_placeholders_in_a_plain_text_part_are_removed()
	{
		var text = "[image: Jobgether] Hello Leo, we have reviewed your application <https://example.com/a/very/long/tracking/url/that/means/nothing?x=1234567890> for the position";

		Assert.Equal("Hello Leo, we have reviewed your application example.com for the position", SnippetBuilder.Build(text, null));
	}

	[Fact]
	public void A_plain_text_part_with_no_real_text_falls_through_to_the_html()
	{
		Assert.Equal(
			"Receipt from Crazy Telecommunications Amount paid",
			SnippetBuilder.Build("[image: logo]", "<p>Receipt from Crazy Telecommunications</p><p>Amount paid</p>")
		);
	}

	[Fact]
	public void Entity_padding_pasted_into_the_plain_text_part_does_not_hide_the_html_body()
	{
		var text = "CommBank" + string.Concat(Enumerable.Repeat("&#847;&zwnj; ", 40));
		const string html = "<p>Your online statement for the account ending in 8862 is now ready to view online.</p>";

		Assert.Equal(
			"Your online statement for the account ending in 8862 is now ready to view online.",
			SnippetBuilder.Build(text, html)
		);
	}

	[Fact]
	public void Nothing_readable_gives_an_empty_preview_rather_than_markup()
	{
		Assert.Equal(string.Empty, SnippetBuilder.Build(null, "<style>.a{color:red}</style><div style=\"display:none\">&zwnj;&zwnj;</div>"));
	}
}
