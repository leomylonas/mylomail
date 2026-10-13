using System.Net;
using MyloMail.Api.Providers;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class GoogleContactSyncTokenTests
{
	/// <summary>
	/// This is the response that left a Google account's contacts failing every five minutes for
	/// days: the token had expired, and the pull treated the refusal as a remote conflict.
	/// </summary>
	[Fact]
	public void The_400_Google_sends_for_an_expired_sync_token_means_start_over_without_it()
	{
		const string body = """
			{"error":{"code":400,"message":"Sync token is expired. Clear local cache and retry call without the sync token.","status":"FAILED_PRECONDITION","details":[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","reason":"EXPIRED_SYNC_TOKEN","domain":"googleapis.com"}]}}
			""";

		Assert.True(GooglePeopleContactProvider.IsExpiredSyncToken(HttpStatusCode.BadRequest, body));
		Assert.True(GooglePeopleContactProvider.IsExpiredSyncToken(HttpStatusCode.Gone, string.Empty));
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"status":"INVALID_ARGUMENT"}}""")]
	[InlineData(HttpStatusCode.Unauthorized, """{"error":{"status":"UNAUTHENTICATED"}}""")]
	[InlineData(HttpStatusCode.Forbidden, """{"error":{"status":"FAILED_PRECONDITION"}}""")]
	[InlineData(HttpStatusCode.TooManyRequests, "{}")]
	public void Other_refusals_are_not_mistaken_for_an_expired_token(HttpStatusCode status, string body) =>
		Assert.False(GooglePeopleContactProvider.IsExpiredSyncToken(status, body));
}
