using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Gmail;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GmailInitialSyncCursorTests
{
	[Fact]
	public void Last_message_bound_is_consumed_across_provider_pages()
	{
		var cursor = GmailMailProvider.InitialSyncCursor.Parse(
			null,
			InitialSyncMode.LastNMessages,
			5
		);
		Assert.Equal(2, cursor.RequestLimit(2));

		var first = cursor.Advance("provider.page/2", 2);
		Assert.True(first.HasMore);
		Assert.NotEqual("provider.page/2", first.ResumeToken);

		cursor = GmailMailProvider.InitialSyncCursor.Parse(
			first.ResumeToken,
			InitialSyncMode.LastNMessages,
			5
		);
		Assert.Equal("provider.page/2", cursor.ProviderPageToken);
		Assert.Equal(3, cursor.Remaining);
		Assert.Equal(2, cursor.RequestLimit(2));

		var second = cursor.Advance("provider.page/3", 2);
		cursor = GmailMailProvider.InitialSyncCursor.Parse(
			second.ResumeToken,
			InitialSyncMode.LastNMessages,
			5
		);
		Assert.Equal(1, cursor.RequestLimit(2));

		var final = cursor.Advance("provider.has.more", 1);
		Assert.False(final.HasMore);
		Assert.Null(final.ResumeToken);
	}

	[Fact]
	public void Legacy_raw_token_is_not_mistaken_for_an_unconsumed_message_bound()
	{
		Assert.Throws<InvalidOperationException>(() =>
			GmailMailProvider.InitialSyncCursor.Parse(
				"legacy-provider-page",
				InitialSyncMode.LastNMessages,
				5
			)
		);
	}

	[Fact]
	public void Unbounded_modes_keep_the_provider_page_token_opaque()
	{
		var cursor = GmailMailProvider.InitialSyncCursor.Parse(
			"provider.page/2",
			InitialSyncMode.Full,
			null
		);

		Assert.Equal("provider.page/2", cursor.ProviderPageToken);
		Assert.Null(cursor.Remaining);
		Assert.Equal(("provider.page/3", true), cursor.Advance("provider.page/3", 200));
	}
}
