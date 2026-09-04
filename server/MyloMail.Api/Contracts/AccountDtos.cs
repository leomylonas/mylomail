using MyloMail.Api.Domain;
using Tapper;

namespace MyloMail.Api.Contracts;

/// <summary>An account as the sidebar shows it (§1, Epic 1).</summary>
/// <remarks>
/// <paramref name="EmailAddress"/> comes from the account's default <c>SendIdentity</c>,
/// which is the authoritative address — <c>Account</c> deliberately has no address column.
/// <para>
/// Carries every field <c>AccountSettings.tsx</c> needs to open pre-filled with what was
/// actually last saved, not a hardcoded default (§1) — this list was previously missing
/// <see cref="PollIntervalSeconds"/> through <see cref="AttachmentSizeLimitOverride"/>
/// entirely, so the settings form always opened showing defaults rather than the account's
/// real values, however recently they were changed.
/// </para>
/// </remarks>
/// <param name="AppendToSentOnSend">
/// Whether to append a Sent copy after sending (§15) — IMAP only, mirroring
/// <see cref="ImapProviderConfig.AppendToSentOnSend"/>. Null for every other provider type,
/// since Gmail/Graph place the copy themselves as part of the send.
/// </param>
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
	int SortOrder,
	bool SidebarCollapsed,
	int PollIntervalSeconds,
	bool PollingEnabled,
	int UndoSendDelaySeconds,
	bool NotificationsEnabled,
	CertificateTrustMode CertificateTrustMode,
	int? AttachmentSizeLimitOverride,
	bool? AppendToSentOnSend
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
	CalDavAccountSettings? CalDav = null,
	/// <summary>
	/// Opt into <see cref="CertificateTrustMode.TrustAll"/> for this account from the moment
	/// it's created (§15) — the only certificate-trust choice available before the account
	/// exists, since pinning a specific fingerprint needs an account id to pin it against.
	/// A retry after a rejected certificate is the only place this is set to anything but
	/// <see cref="CertificateTrustMode.Default"/>.
	/// </summary>
	CertificateTrustMode CertificateTrustMode = CertificateTrustMode.Default,
	/// <summary>
	/// Bounded (last N months/messages) or full-history initial sync (§3, §13 Epic 3) — set
	/// once, at creation; there is no later "re-bound an existing account" operation.
	/// </summary>
	InitialSyncMode InitialSyncMode = InitialSyncMode.Full,
	/// <summary>Required when <see cref="InitialSyncMode"/> is not <see cref="Domain.InitialSyncMode.Full"/>.</summary>
	int? InitialSyncBoundValue = null
);

/// <summary>
/// A request to re-verify an account stuck in <c>NeedsReauth</c> or <c>Error</c> (§3).
/// </summary>
/// <remarks>
/// <paramref name="Secret"/> is optional: a certificate-only rejection (the server's cert
/// rotated, since pinned via <c>TrustCertificate</c>) needs nothing supplied at all — the
/// existing stored credential is still correct, only the server's identity changed.
/// </remarks>
[TranspilationSource]
public record ReauthenticateAccountRequest(string? Secret);

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
	string? SmtpSecret = null,
	string? SmtpUserName = null
);

[TranspilationSource]
public record CalDavAccountSettings(string Endpoint, string UserName, bool ReuseImapCredential, string? Secret);
