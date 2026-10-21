using MyloMail.Api.Domain;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

public sealed class DraftSenderTests
{
	[Fact]
	public void The_identitys_name_is_sent_with_its_address()
	{
		var draft = new Draft
		{
			FromAddress = "leo@example.org",
			FromName = Draft.SenderName("Leo Mylonas", "leo@example.org"),
		};

		Assert.Equal("Leo Mylonas <leo@example.org>", draft.FromMailbox().ToString(true));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	[InlineData("LEO@example.org")]
	public void A_blank_name_or_the_address_repeated_sends_the_bare_address(string displayName)
	{
		var draft = new Draft
		{
			FromAddress = "leo@example.org",
			FromName = Draft.SenderName(displayName, "leo@example.org"),
		};

		Assert.Equal("leo@example.org", draft.FromMailbox().ToString(true));
	}
}
