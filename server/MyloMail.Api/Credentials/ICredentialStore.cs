namespace MyloMail.Api.Credentials;

/// <summary>
/// Durable storage for opaque provider credential payloads (§4).
/// Implementations select an OS keychain where available and never place credentials on an
/// <c>Account</c> or in ordinary SQLite columns.
/// </summary>
public interface ICredentialStore
{
	Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct);

	Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct);

	Task DeleteAsync(Guid accountId, CancellationToken ct);
}

/// <summary>
/// An opaque provider-owned credential cache. The store deliberately does not understand
/// access tokens, refresh tokens, or an SDK's cache layout: interpreting those here would
/// couple the security boundary to a provider implementation.
/// </summary>
public sealed record CredentialPayload(string Format, byte[] Data);
