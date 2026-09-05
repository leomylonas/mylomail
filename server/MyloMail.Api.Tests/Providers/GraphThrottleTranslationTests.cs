using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// §15's throttling requirement, Graph's side. Unlike Gmail's <see cref="GoogleApiException"/>,
/// Kiota's <see cref="ApiException"/> already carries the raw response headers, so
/// <see cref="GraphThrottleAwareRequests.RetryAfterOf"/> and
/// <see cref="GraphThrottleAwareRequests.Translate"/> can be exercised directly against a real
/// constructed exception - no fake HTTP transport needed.
/// </summary>
public sealed class GraphThrottleTranslationTests
{
	[Fact]
	public void RetryAfterOf_reads_a_numeric_seconds_header()
	{
		var ex = ApiExceptionWithHeaders(new Dictionary<string, IEnumerable<string>> { ["Retry-After"] = ["20"] });

		Assert.Equal(TimeSpan.FromSeconds(20), GraphThrottleAwareRequests.RetryAfterOf(ex));
	}

	[Fact]
	public void RetryAfterOf_is_case_insensitive_about_the_header_name()
	{
		var ex = ApiExceptionWithHeaders(new Dictionary<string, IEnumerable<string>> { ["retry-after"] = ["7"] });

		Assert.Equal(TimeSpan.FromSeconds(7), GraphThrottleAwareRequests.RetryAfterOf(ex));
	}

	[Fact]
	public void RetryAfterOf_returns_null_when_the_header_is_absent()
	{
		var ex = ApiExceptionWithHeaders(new Dictionary<string, IEnumerable<string>>());

		Assert.Null(GraphThrottleAwareRequests.RetryAfterOf(ex));
	}

	[Fact]
	public void Translate_uses_the_real_header_value_when_present()
	{
		var ex = ApiExceptionWithHeaders(new Dictionary<string, IEnumerable<string>> { ["Retry-After"] = ["20"] });

		Assert.Equal(TimeSpan.FromSeconds(20), GraphThrottleAwareRequests.Translate(ex).RetryAfter);
	}

	/// <summary>
	/// Manually confirmed as a genuine discriminator: reverting <see cref="GraphThrottleAwareRequests.Translate"/>
	/// to always use <see cref="GraphThrottleAwareRequests.DefaultRetryAfter"/> (ignoring a real header)
	/// makes this fail, since the real header value (20s) differs from the default (30s).
	/// </summary>
	[Fact]
	public void Translate_falls_back_to_the_default_when_no_header_is_present()
	{
		var ex = ApiExceptionWithHeaders(new Dictionary<string, IEnumerable<string>>());

		Assert.Equal(GraphThrottleAwareRequests.DefaultRetryAfter, GraphThrottleAwareRequests.Translate(ex).RetryAfter);
	}

	private static ApiException ApiExceptionWithHeaders(IDictionary<string, IEnumerable<string>> headers) =>
		new("throttled") { ResponseStatusCode = 429, ResponseHeaders = headers };
}
