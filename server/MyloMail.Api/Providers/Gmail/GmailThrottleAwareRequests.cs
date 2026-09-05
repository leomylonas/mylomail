using System.Net;
using System.Runtime.CompilerServices;
using Google;
using Google.Apis.Gmail.v1;
using Google.Apis.Http;
using Google.Apis.Requests;
using Google.Apis.Services;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// Captures a 429 response's <c>Retry-After</c> header before <see cref="GoogleApiException"/>
/// discards it — the exception type this SDK version throws for a failed request carries only
/// an <see cref="GoogleApiException.HttpStatusCode"/>, with no way to read response headers
/// (§15: "honour the provider's explicit signal ... as the exact retry delay").
/// </summary>
/// <remarks>
/// One instance is attached to each <see cref="Google.Apis.Services.BaseClientService"/> when it
/// is constructed and lives for that service's lifetime (one Gmail operation's worth of calls,
/// since <c>GmailMailProvider</c> builds a fresh service per call rather than caching one).
/// Returning <c>false</c> from <see cref="HandleResponseAsync"/> leaves Google.Apis's own
/// handling of the response entirely unchanged — this only observes it.
/// </remarks>
internal sealed class GmailThrottleTracker : IHttpUnsuccessfulResponseHandler
{
	public TimeSpan? LastRetryAfter { get; private set; }

	public Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
	{
		if (args.Response.StatusCode == (HttpStatusCode)429 && args.Response.Headers.RetryAfter is { } retryAfter)
		{
			LastRetryAfter = retryAfter.Delta ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null);
		}

		return Task.FromResult(false);
	}
}

/// <summary>
/// Every <c>GmailMailProvider</c> operation routes its request through
/// <see cref="ExecuteThrottleAwareAsync{TResponse}"/> instead of calling
/// <see cref="IClientServiceRequest{TResponse}.ExecuteAsync(CancellationToken)"/> directly, so a
/// 429 response is translated into <see cref="ProviderThrottledException"/> uniformly across
/// every call site rather than depending on each of the ~19 sites remembering to catch it
/// individually (the exact "producer path added once, but only where someone thought of it"
/// shape passes 196-198 found and fixed for auth rejection).
/// </summary>
internal static class GmailRequestExtensions
{
	/// <summary>
	/// Gmail's 429 response carries no documented <c>Retry-After</c> header (unlike Graph's
	/// throttling response) - Google's own guidance for this API is exponential backoff, not an
	/// explicit delay. When the header is genuinely absent despite the 429 status, this is the
	/// fallback delay; if a future response does carry the header, that value is used instead.
	/// </summary>
	internal static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

	// ConfigurableMessageHandler.UnsuccessfulResponseHandlers itself is obsolete to read, only
	// Add/RemoveUnsuccessfulResponseHandler remain supported - so the tracker created alongside
	// each service is looked up here instead of re-read off the handler chain.
	private static readonly ConditionalWeakTable<IClientService, GmailThrottleTracker> Trackers = new();

	public static void AttachThrottleTracker(this GmailService service, GmailThrottleTracker tracker)
	{
		service.HttpClient.MessageHandler.AddUnsuccessfulResponseHandler(tracker);
		Trackers.Add(service, tracker);
	}

	public static async Task<TResponse> ExecuteThrottleAwareAsync<TResponse>(
		this IClientServiceRequest<TResponse> request,
		CancellationToken ct
	)
	{
		try
		{
			return await request.ExecuteAsync(ct);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.TooManyRequests)
		{
			Trackers.TryGetValue(request.Service, out var tracker);
			throw Translate(ex, tracker?.LastRetryAfter);
		}
	}

	/// <summary>Extracted so a test can exercise the fallback-to-default logic directly, without
	/// needing a live 429 response to reach it.</summary>
	internal static ProviderThrottledException Translate(GoogleApiException ex, TimeSpan? captured) =>
		new(captured is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : DefaultRetryAfter, "Gmail rate limit exceeded.", ex);
}
