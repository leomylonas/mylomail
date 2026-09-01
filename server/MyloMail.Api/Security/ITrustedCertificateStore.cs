using MyloMail.Api.Domain;

namespace MyloMail.Api.Security;

/// <summary>
/// Reads and writes <see cref="AccountTrustedCertificate"/> (§1, §15).
/// </summary>
/// <remarks>
/// <see cref="GetForAccount"/> is synchronous and called from provider factories that already
/// resolve credentials the same way (<c>ICredentialStore</c> calls blocked on with
/// <c>GetAwaiter().GetResult()</c>) — a provider is handed the resolved list once at
/// construction, not left to query it from inside a synchronous TLS validation callback, where
/// blocking on I/O would risk a deadlock under the wrong synchronization context.
/// </remarks>
public interface ITrustedCertificateStore
{
	IReadOnlyList<AccountTrustedCertificate> GetForAccount(Guid accountId);

	/// <summary>Records a certificate as trusted, if it is not already (§7, <c>TrustCertificate</c>).</summary>
	Task TrustAsync(Guid accountId, string expectedHostname, string sha256Fingerprint, CancellationToken ct);
}
