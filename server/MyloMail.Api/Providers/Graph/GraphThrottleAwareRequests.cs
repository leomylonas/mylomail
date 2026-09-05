using Microsoft.Kiota.Abstractions;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Every <c>GraphMailProvider</c> operation routes its request through one of these two
/// overloads instead of calling the Graph fluent builder's own <c>GetAsync</c>/<c>PostAsync</c>/
/// etc. directly, so a throttled (429) response is translated into
/// <see cref="ProviderThrottledException"/> uniformly across every call site rather than
/// depending on each of the ~20 sites remembering to catch it individually (the exact "producer
/// path added once, but only where someone thought of it" shape passes 196-198 found and fixed
/// for auth rejection). Unlike Gmail's <see cref="GmailRequestExtensions"/>, no separate response
/// tracker is needed: Kiota's <see cref="ApiException"/> already exposes the raw response headers.
/// </summary>
internal static class GraphThrottleAwareRequests
{
	/// <summary>
	/// Used only when a 429 response genuinely carries no <c>Retry-After</c> header despite
	/// Microsoft Graph's own throttling guidance documenting that it always sends one - a
	/// defensive fallback, not the expected case.
	/// </summary>
	internal static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(30);

	public static async Task<T> ThrottleAwareAsync<T>(Func<Task<T>> operation)
	{
		try
		{
			return await operation();
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 429)
		{
			throw Translate(ex);
		}
	}

	public static async Task ThrottleAwareAsync(Func<Task> operation)
	{
		try
		{
			await operation();
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 429)
		{
			throw Translate(ex);
		}
	}

	internal static ProviderThrottledException Translate(ApiException ex) =>
		new(RetryAfterOf(ex) is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : DefaultRetryAfter,
			"Microsoft Graph throttled this request.",
			ex);

	internal static TimeSpan? RetryAfterOf(ApiException ex)
	{
		var header = ex
			.ResponseHeaders?.Keys.FirstOrDefault(key => string.Equals(key, "Retry-After", StringComparison.OrdinalIgnoreCase));
		var raw = header is not null ? ex.ResponseHeaders![header].FirstOrDefault() : null;
		if (raw is null)
		{
			return null;
		}

		if (int.TryParse(raw, out var seconds))
		{
			return TimeSpan.FromSeconds(seconds);
		}

		return DateTimeOffset.TryParse(raw, out var date) ? date - DateTimeOffset.UtcNow : null;
	}
}
