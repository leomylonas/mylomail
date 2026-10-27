using MyloMail.Api.Content;
using Xunit;

namespace MyloMail.Api.Tests.Content;

public sealed class HtmlToTextTests
{
	[Fact]
	public void An_invitation_body_becomes_its_words_with_paragraphs_kept()
	{
		const string html = "<html>\r\n<head>\r\n<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\r\n</head>\r\n<body><p>Testing &amp; more</p><p>Second<br>line</p></body></html>";

		Assert.Equal("Testing & more\n\nSecond\nline", HtmlToText.Convert(html));
	}
}
