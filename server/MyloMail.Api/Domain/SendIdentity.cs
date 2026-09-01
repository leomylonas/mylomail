namespace MyloMail.Api.Domain;

/// <summary>
/// A send-as identity (§1). The default identity's <see cref="EmailAddress"/> is the
/// authoritative address for the account — <see cref="Account"/> deliberately has no
/// address column, to avoid two sources of truth.
/// </summary>
public class SendIdentity
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public string DisplayName { get; set; } = string.Empty;

	/// <summary>The send-as address — an alias, or the primary address.</summary>
	public string EmailAddress { get; set; } = string.Empty;

	public string? SignatureHtml { get; set; }

	/// <summary>Exactly one default per account.</summary>
	public bool IsDefault { get; set; }
}

/// <summary>
/// A certificate the user has chosen to trust for this account. Multiple entries are
/// supported, because certificates rotate (§1, §15).
/// </summary>
/// <remarks>
/// Pinned by <see cref="ExpectedHostname"/> as well as fingerprint: a fingerprint alone would
/// let a certificate trusted for one host silently vouch for an unrelated one presenting the
/// same bytes, which defeats the point of pinning to begin with.
/// </remarks>
public class AccountTrustedCertificate
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>Lowercase hex SHA-256 of the certificate's raw DER bytes — never an ambiguously-defined "thumbprint".</summary>
	public string Sha256Fingerprint { get; set; } = string.Empty;

	public string ExpectedHostname { get; set; } = string.Empty;
}
