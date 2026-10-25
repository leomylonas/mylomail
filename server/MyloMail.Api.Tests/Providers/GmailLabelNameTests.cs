using Google.Apis.Gmail.v1.Data;
using MyloMail.Api.Providers.Gmail;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GmailLabelNameTests
{
	[Theory]
	[InlineData("INBOX", "Inbox")]
	[InlineData("DRAFT", "Drafts")]
	[InlineData("SPAM", "Spam")]
	[InlineData("CATEGORY_PROMOTIONS", "Category Promotions")]
	public void System_labels_are_shown_as_words(string name, string expected) =>
		Assert.Equal(expected, GmailMailProvider.DisplayName(new Label { Name = name, Type = "system" }));

	[Fact]
	public void A_users_own_label_is_left_as_typed() =>
		Assert.Equal("TAX 2026", GmailMailProvider.DisplayName(new Label { Name = "TAX 2026", Type = "user" }));
}

public sealed class GraphDeltaPartialTests
{
	[Fact]
	public void A_delta_entry_with_only_changed_properties_is_partial_and_a_whole_message_is_not()
	{
		Assert.True(MyloMail.Api.Providers.Graph.GraphMailProvider.IsPartial(new Microsoft.Graph.Models.Message { Id = "x", IsRead = true }));
		Assert.False(MyloMail.Api.Providers.Graph.GraphMailProvider.IsPartial(new Microsoft.Graph.Models.Message { Id = "x", Subject = "Hi" }));
	}
}
