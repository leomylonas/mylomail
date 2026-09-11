using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GraphInitialSyncCursorTests
{
	[Fact]
	public void Last_message_bound_survives_absolute_next_links_and_short_final_page()
	{
		var cursor = GraphMailProvider.InitialSyncCursor.Parse(
			null,
			InitialSyncMode.LastNMessages,
			5
		);
		Assert.Equal(2, cursor.RequestLimit(2));

		var first = cursor.Advance("https://graph.microsoft.test/messages?$skiptoken=two", 2);
		Assert.True(first.HasMore);
		Assert.NotEqual("https://graph.microsoft.test/messages?$skiptoken=two", first.ResumeToken);

		cursor = GraphMailProvider.InitialSyncCursor.Parse(
			first.ResumeToken,
			InitialSyncMode.LastNMessages,
			5
		);
		Assert.Equal("https://graph.microsoft.test/messages?$skiptoken=two", cursor.ProviderNextLink);
		Assert.Equal(3, cursor.Remaining);

		var second = cursor.Advance("https://graph.microsoft.test/messages?$skiptoken=three", 2);
		cursor = GraphMailProvider.InitialSyncCursor.Parse(
			second.ResumeToken,
			InitialSyncMode.LastNMessages,
			5
		);
		Assert.Equal(1, cursor.RequestLimit(2));

		var final = cursor.Advance("https://graph.microsoft.test/messages?$skiptoken=unused", 1);
		Assert.False(final.HasMore);
		Assert.Null(final.ResumeToken);
	}

	[Fact]
	public void Legacy_raw_next_link_is_rejected_only_for_count_bounded_walks()
	{
		Assert.Throws<InvalidOperationException>(() =>
			GraphMailProvider.InitialSyncCursor.Parse(
				"https://graph.microsoft.test/messages?$skiptoken=legacy",
				InitialSyncMode.LastNMessages,
				5
			)
		);

		var months = GraphMailProvider.InitialSyncCursor.Parse(
			"https://graph.microsoft.test/messages?$skiptoken=months",
			InitialSyncMode.LastNMonths,
			3
		);
		Assert.Equal(
			"https://graph.microsoft.test/messages?$skiptoken=months",
			months.ProviderNextLink
		);
		Assert.Null(months.Remaining);
	}
}
