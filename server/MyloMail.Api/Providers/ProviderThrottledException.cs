using MyloMail.Api.Errors;

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
/// Provider access cannot proceed until account-level intervention completes.
/// </summary>
/// <remarks>
/// Ordinary credential rejection carries <see cref="Domain.AuthState.NeedsReauth"/>.
/// Structured validation failures such as administrator consent or certificate trust carry
/// <see cref="Domain.AuthState.Error"/> instead: asking the user to re-enter a valid password
/// is not the remedy, but the account still must stop retrying in the background.
/// </remarks>
public class ProviderAuthenticationException : Exception
{
	public ProviderAuthenticationException(string message, Exception? inner = null)
		: base(message, inner)
	{
		AccountState = Domain.AuthState.NeedsReauth;
	}

	/// <summary>
	/// Keeps a provider's actionable rejection intact at asynchronous transport boundaries,
	/// where reducing it to <see cref="Exception.Message"/> would discard structured recovery
	/// data such as an untrusted certificate's pin target.
	/// </summary>
	public ProviderAuthenticationException(MutationProblemDetails problem, Exception? inner = null)
		: base(problem.Detail ?? problem.Title ?? "Authentication was rejected.", inner)
	{
		Problem = problem;
		AccountState = problem.Category == ErrorCategory.Auth
			? Domain.AuthState.NeedsReauth
			: Domain.AuthState.Error;
	}

	public Domain.AuthState AccountState { get; }

	public MutationProblemDetails? Problem { get; }
}
