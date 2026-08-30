namespace MyloMail.Api.Providers;

/// <summary>
/// The provider gave an explicit throttling signal and said when to come back (§15).
/// </summary>
/// <remarks>
/// This exists so the scheduler can honour <paramref name="retryAfter"/> exactly rather than
/// applying a generic backoff curve on top of it. Gmail's <c>429</c>/<c>Retry-After</c> and
/// Graph's throttling responses carry a specific delay; guessing a longer one wastes time
/// the provider said was available, and guessing a shorter one gets throttled again.
/// <para>
/// IMAP has no standard rate-limit signalling, so it falls back to exponential backoff —
/// there is no signal to honour, which is a different situation from a signal being ignored.
/// </para>
/// </remarks>
public class ProviderThrottledException(TimeSpan retryAfter, string message, Exception? inner = null)
	: Exception(message, inner)
{
	public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Authentication failed and will not succeed on retry until the user intervenes.
/// </summary>
/// <remarks>
/// Distinguished from every other failure because the response is the opposite of a retry:
/// the account is paused and marked <see cref="Domain.AuthState.NeedsReauth"/>. Retrying an
/// auth failure burns the user's remaining attempts against some providers, and none of them
/// recover on their own.
/// </remarks>
public class ProviderAuthenticationException(string message, Exception? inner = null)
	: Exception(message, inner);
