using Google;
using Google.Apis.Http;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Gmail;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// §15's throttling requirement, Gmail's side: a 429 response must translate to
/// <see cref="ProviderThrottledException"/> carrying an explicit retry delay, rather than
/// letting <see cref="GoogleApiException"/> propagate raw and fall into generic backoff. Driving
/// a real 429 through a live <c>GmailService</c> would need a fake HTTP transport this codebase
/// has no seam for (the request pipeline is built internally from
/// <see cref="Google.Apis.Services.BaseClientService.Initializer"/>), so these tests exercise the
/// two real, separable pieces directly: the header-capturing tracker, and the fallback logic
/// that picks a captured delay over the default when one was actually observed.
/// </summary>
public sealed class GmailThrottleTranslationTests
{
	[Fact]
	public async Task Tracker_captures_the_retry_after_delta_from_a_429_response()
	{
		var tracker = new GmailThrottleTracker();
		using var response = new HttpResponseMessage((System.Net.HttpStatusCode)429);
		response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));

		await tracker.HandleResponseAsync(new HandleUnsuccessfulResponseArgs { Response = response });

		Assert.Equal(TimeSpan.FromSeconds(42), tracker.LastRetryAfter);
	}

	[Fact]
	public async Task Tracker_ignores_a_retry_after_header_on_a_non_429_response()
	{
		var tracker = new GmailThrottleTracker();
		using var response = new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
		response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));

		await tracker.HandleResponseAsync(new HandleUnsuccessfulResponseArgs { Response = response });

		Assert.Null(tracker.LastRetryAfter);
	}

	[Fact]
	public void Translate_uses_the_captured_delay_when_the_response_carried_one()
	{
		var ex = new GoogleApiException("gmail", "rate limited") { HttpStatusCode = System.Net.HttpStatusCode.TooManyRequests };

		var translated = GmailRequestExtensions.Translate(ex, TimeSpan.FromSeconds(15));

		Assert.Equal(TimeSpan.FromSeconds(15), translated.RetryAfter);
	}

	/// <summary>
	/// Manually confirmed as a genuine discriminator: this is the fallback branch a lowercase-Z-
	/// style regression would silently skip - passing <c>TimeSpan.Zero</c> (a technically non-null
	/// but useless value, matching what a captured-but-already-elapsed header would produce)
	/// through unchanged would schedule an immediate retry instead of backing off at all.
	/// </summary>
	[Fact]
	public void Translate_falls_back_to_the_default_when_no_delay_was_captured()
	{
		var ex = new GoogleApiException("gmail", "rate limited") { HttpStatusCode = System.Net.HttpStatusCode.TooManyRequests };

		Assert.Equal(GmailRequestExtensions.DefaultRetryAfter, GmailRequestExtensions.Translate(ex, null).RetryAfter);
		Assert.Equal(GmailRequestExtensions.DefaultRetryAfter, GmailRequestExtensions.Translate(ex, TimeSpan.Zero).RetryAfter);
	}
}
