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

	/// <summary>Non-secret configuration only — host/port, tenant id, IMAP auth method.</summary>
	public ProviderConfig? ProviderConfig { get; set; }

	public int PollIntervalSeconds { get; set; }
	public bool PollingEnabled { get; set; } = true;
	public InitialSyncMode InitialSyncMode { get; set; }

	/// <summary>Null when <see cref="InitialSyncMode"/> is <see cref="InitialSyncMode.Full"/>.</summary>
	public int? InitialSyncBoundValue { get; set; }

	/// <summary>Per-account undo-send window; 0 means send immediately.</summary>
	public int UndoSendDelaySeconds { get; set; }

	public bool NotificationsEnabled { get; set; } = true;

	/// <summary>Fallback used when the provider cannot report a limit (§15).</summary>
	public int? AttachmentSizeLimitOverride { get; set; }

	public CertificateTrustMode CertificateTrustMode { get; set; }
}

/// <summary>Non-secret, provider-specific account configuration. Never carries credentials.</summary>
public abstract class ProviderConfig;

public sealed class ImapProviderConfig : ProviderConfig
{
	public string Host { get; set; } = string.Empty;
	public int Port { get; set; }
	public bool UseSsl { get; set; } = true;
	public string AuthMethod { get; set; } = string.Empty;
	public string SmtpHost { get; set; } = string.Empty;
	public int SmtpPort { get; set; }
}

public sealed class GmailProviderConfig : ProviderConfig;

public sealed class Microsoft365ProviderConfig : ProviderConfig
{
	public string? TenantId { get; set; }
}
