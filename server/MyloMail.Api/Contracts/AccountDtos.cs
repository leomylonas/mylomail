using MyloMail.Api.Domain;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>An account as the sidebar shows it (§1, Epic 1).</summary>
/// <remarks>
/// <paramref name="EmailAddress"/> comes from the account's default <c>SendIdentity</c>,
/// which is the authoritative address — <c>Account</c> deliberately has no address column.
/// </remarks>
[TranspilationSource]
public record AccountDto(
	Guid Id,
	string DisplayName,
	string EmailAddress,
	ProviderType ProviderType,
	AuthState AuthState,
	bool IsEnabled,
	string? LastAuthError,
	string Color,
	int SortOrder
);

/// <summary>
/// A request to add an account.
/// </summary>
/// <remarks>
/// <paramref name="Secret"/> is the password or token payload. It is passed straight to the
/// credential store and never written to a column, so it appears here and nowhere in the
/// resource model (§4).
/// </remarks>
[TranspilationSource]
public record AddAccountRequest(
	string DisplayName,
	ProviderType ProviderType,
	string EmailAddress,
	string? Secret,
	ImapAccountSettings? Imap,
	CalDavAccountSettings? CalDav = null
);

/// <summary>
/// The non-secret configuration an IMAP account needs.
/// </summary>
/// <remarks>
/// <paramref name="UserName"/> is the login name, deliberately separate from the account's
/// address: servers commonly authenticate on a bare user name, or on an address that differs
/// from the send-as one.
/// </remarks>
[TranspilationSource]
public record ImapAccountSettings(
	string Host,
	int Port,
	bool UseSsl,
	string UserName,
	string SmtpHost,
	int SmtpPort,
	bool ReuseImapCredentialForSmtp = true,
	string? SmtpSecret = null
);

[TranspilationSource]
public record CalDavAccountSettings(string Endpoint, string UserName, bool ReuseImapCredential, string? Secret);
