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
	public void A_delta_entry_reports_only_the_groups_of_properties_it_carries()
	{
		var dto = MyloMail.Api.Providers.Graph.GraphMailProvider.ToDto(
			new Microsoft.Graph.Models.Message { Id = "x", IsRead = true },
			"folder-1"
		);

		Assert.Equal([MyloMail.Api.Providers.Contracts.MessageProperty.IsRead], dto.Reported);
		Assert.Equal("folder-1", dto.Occurrences.Single().ProviderMailboxId);
	}
}
