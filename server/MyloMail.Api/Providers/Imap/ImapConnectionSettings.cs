using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Everything needed to open one IMAP session.
/// </summary>
/// <remarks>
/// <b>This carries a password, and is therefore a stand-in.</b> §4 puts credentials in an
/// OS credential store resolved at use time, never on <c>Account</c> and never in
/// configuration. When that store exists, this record keeps the non-secret fields and the
/// secret is resolved through it. Until then this exists so the provider can be exercised
/// against the local capability matrix.
/// </remarks>
public sealed record ImapConnectionSettings(
	string Host,
	int Port,
	bool UseSsl,
	string UserName,
	string Password,
	string SmtpHost = "",
	int SmtpPort = 0,
	string? SmtpUserName = null,
	string? SmtpPassword = null,
	/// <summary>
	/// Whether to append a copy to the Sent mailbox after sending (§15).
	/// </summary>
	/// <remarks>
	/// SMTP relays a message and does not file it, so the client must append the copy — but
	/// some servers, notably Gmail over IMAP, do it themselves, and appending there would
	/// duplicate it. There is no way to detect which, so it is configuration.
	/// </remarks>
	bool AppendToSent = true,
	CertificateTrustMode CertificateTrustMode = CertificateTrustMode.Default,
	/// <summary>Resolved once at construction (§15) — see <see cref="Security.ITrustedCertificateStore"/>.</summary>
	IReadOnlyList<AccountTrustedCertificate>? TrustedCertificates = null
);
