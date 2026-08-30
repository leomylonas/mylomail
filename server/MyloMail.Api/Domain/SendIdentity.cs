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
public class AccountTrustedCertificate
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public string Thumbprint { get; set; } = string.Empty;
}
