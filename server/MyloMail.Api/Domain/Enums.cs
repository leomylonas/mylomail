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
