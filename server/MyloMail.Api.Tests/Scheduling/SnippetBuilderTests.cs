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
	public void Nothing_readable_gives_an_empty_preview_rather_than_markup()
	{
		Assert.Equal(string.Empty, SnippetBuilder.Build(null, "<style>.a{color:red}</style><div style=\"display:none\">&zwnj;&zwnj;</div>"));
	}
}
