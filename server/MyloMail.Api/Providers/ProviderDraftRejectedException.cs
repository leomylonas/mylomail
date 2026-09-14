using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers;

/// <summary>
/// The provider created or retained a remote draft, but rejected a requested draft property.
/// </summary>
/// <remarks>
/// <see cref="DraftResult"/> preserves the remote identity and revision so draft synchronization
/// can durably record the rejected remote copy rather than treating the completed create as an
/// ambiguous or unsupported operation.
/// </remarks>
public sealed class ProviderDraftRejectedException(
	string message,
	DraftResult draft,
	Exception? innerException = null
) : Exception(message, innerException)
{
	public DraftResult Draft { get; } = draft;
}
