using Tapper;
namespace MyloMail.Api.Domain;

[TranspilationSource]
public enum ProviderType
{
	Imap,
	Gmail,
	Microsoft365,
}

/// <summary>
/// Authentication state only. Sync progress lives in the sync-state tables (§1), never here.
/// </summary>
[TranspilationSource]
public enum AuthState
{
	Connected,
	NeedsReauth,
	Error,
	/// <summary>
	/// A mid-session read from the OS credential store failed (a locked keyring, a denied
	/// Keychain prompt) — distinct from <see cref="NeedsReauth"/> because the stored
	/// credentials are not wrong; re-entering them would not help. The fix is to unlock the
	/// OS store, not to reauthenticate.
	/// </summary>
	CredentialStoreUnavailable,
}

[TranspilationSource]
public enum SpecialUse
{
	None,
	Inbox,
	Sent,
	Drafts,
	Trash,
	Junk,
	Archive,
}

[TranspilationSource]
public enum InitialSyncMode
{
	LastNMonths,
	LastNMessages,
	Full,
}

[TranspilationSource]
public enum CertificateTrustMode
{
	Default,
	TrustAll,
}

[TranspilationSource]
public enum MailTransportSecurity
{
	None,
	TlsOnConnect,
	StartTls,
}

[TranspilationSource]
public enum ImapAuthMethod
{
	Password,
	OAuth2,
}

[TranspilationSource]
public enum SmtpAuthMethod
{
	None,
	Password,
	OAuth2,
}
