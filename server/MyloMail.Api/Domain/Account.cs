using System.Text.Json.Serialization;

namespace MyloMail.Api.Domain;

/// <summary>
/// An account (§1). Credentials are never stored here — see §4. The authoritative email
/// address for the account is the default <see cref="SendIdentity"/>'s address; there is
/// deliberately no <c>EmailAddress</c> column, to avoid two sources of truth.
/// </summary>
public class Account
{
	public Guid Id { get; set; }
	public string DisplayName { get; set; } = string.Empty;
	public ProviderType ProviderType { get; set; }
	public AuthState AuthState { get; set; }

	/// <summary>
	/// Soft-disable. Set false before account removal so running jobs stop cleanly (§3).
	/// Disabled accounts are excluded from scheduling and hidden from the sidebar.
	/// </summary>
	public bool IsEnabled { get; set; } = true;

	public string? LastAuthError { get; set; }
	public string Color { get; set; } = string.Empty;
	public int SortOrder { get; set; }

	/// <summary>Sidebar section collapse (§13 Epic 2). Local UI preference only — never
	/// pushed upstream, the same as <see cref="Mailbox.IsCollapsed"/>.</summary>
	public bool SidebarCollapsed { get; set; }

	/// <summary>Non-secret configuration only — host/port, tenant id, IMAP auth method.</summary>
	public ProviderConfig? ProviderConfig { get; set; }

	public int PollIntervalSeconds { get; set; }
	public bool PollingEnabled { get; set; } = true;

	/// <summary>
	/// Provider contact cursor committed with the observations it advances past. Currently used
	/// by Google People so resource-name changes and deletions are applied incrementally.
	/// </summary>
	public string? ContactSyncCursor { get; set; }
	public InitialSyncMode InitialSyncMode { get; set; }

	/// <summary>Null when <see cref="InitialSyncMode"/> is <see cref="InitialSyncMode.Full"/>.</summary>
	public int? InitialSyncBoundValue { get; set; }

	/// <summary>Per-account undo-send window; 0 means send immediately.</summary>
	public int UndoSendDelaySeconds { get; set; }

	public bool NotificationsEnabled { get; set; } = true;

	/// <summary>
	/// Account-level notification policy: never notify for a message dated before this
	/// instant (§13 Epic 9). Set once at account creation and purely local — it never
	/// changes, unlike the per-stream baseline token each change stream keeps for the same
	/// purpose at its own scope.
	/// </summary>
	public DateTimeOffset NotificationEpoch { get; set; }

	/// <summary>Fallback used when the provider cannot report a limit (§15).</summary>
	public int? AttachmentSizeLimitOverride { get; set; }

	public CertificateTrustMode CertificateTrustMode { get; set; }
}

/// <summary>Non-secret, provider-specific account configuration. Never carries credentials.</summary>
/// <remarks>
/// Persisted as JSON on <see cref="Account"/>. The discriminator is declared here rather
/// than in the persistence layer so that a new configuration shape cannot be added without
/// also giving it a stable stored name.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$providerConfig")]
[JsonDerivedType(typeof(ImapProviderConfig), "imap")]
[JsonDerivedType(typeof(GmailProviderConfig), "gmail")]
[JsonDerivedType(typeof(Microsoft365ProviderConfig), "microsoft365")]
public abstract class ProviderConfig;

public sealed class ImapProviderConfig : ProviderConfig
{
	public string Host { get; set; } = string.Empty;
	public int Port { get; set; }
	public MailTransportSecurity ImapSecurity { get; set; } = MailTransportSecurity.TlsOnConnect;

	/// <summary>
	/// The login name. Deliberately separate from the account's address (the default
	/// <see cref="SendIdentity"/>): plenty of servers authenticate on a bare user name, or on
	/// an address that differs from the one mail is sent as, so deriving it would break those
	/// accounts and give a confusing failure when it did.
	/// </summary>
	public string UserName { get; set; } = string.Empty;

	public ImapAuthMethod AuthMethod { get; set; } = ImapAuthMethod.Password;
	public string SmtpHost { get; set; } = string.Empty;
	public int SmtpPort { get; set; }
	public MailTransportSecurity SmtpSecurity { get; set; } = MailTransportSecurity.StartTls;
	public SmtpAuthMethod SmtpAuthMethod { get; set; } = SmtpAuthMethod.Password;

	/// <summary>
	/// Whether to append a copy to the Sent mailbox after sending. IMAP only, and true by
	/// default (§15).
	/// </summary>
	/// <remarks>
	/// SMTP relays a message and does not file it, so the client must append the copy — but
	/// some servers, notably Gmail over IMAP, do it server-side anyway, which is exactly why
	/// this cannot be hardcoded either way. Gmail and Graph place the copy themselves as part
	/// of the send, and appending there would duplicate it.
	/// </remarks>
	public bool AppendToSentOnSend { get; set; } = true;

	/// <summary>SMTP may reuse IMAP's credential or select its own secure-store slot.</summary>
	public CredentialSource SmtpCredentialSource { get; set; } = CredentialSource.ReuseImap;

	/// <summary>SMTP uses the IMAP user name unless it has an independently configured login.</summary>
	public string? SmtpUserName { get; set; }

	/// <summary>
	/// Optional independently configured CalDAV endpoint. IMAP alone never implies a calendar.
	/// </summary>
	public CalDavProviderConfig? CalDav { get; set; }
}

public sealed class CalDavProviderConfig
{
	public string Endpoint { get; set; } = string.Empty;
	public string UserName { get; set; } = string.Empty;
	public CredentialSource CredentialSource { get; set; } = CredentialSource.ReuseImap;
}

public enum CredentialSource
{
	ReuseImap,
	Independent,
}

public sealed class GmailProviderConfig : ProviderConfig
{
	/// <summary>
	/// Optional per-account installed-app client id for bring-your-own-credentials (§5).
	/// The matching client secret stays in <c>ICredentialStore</c>; a null value selects the
	/// deployment registration.
	/// </summary>
	public string? ClientId { get; set; }
}

public sealed class Microsoft365ProviderConfig : ProviderConfig
{
	public string? TenantId { get; set; }
}
