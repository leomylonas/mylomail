namespace MyloMail.Api.Domain;

public enum ProviderType
{
	Imap,
	Gmail,
	Microsoft365,
}

/// <summary>
/// Authentication state only. Sync progress lives in the sync-state tables (§1), never here.
/// </summary>
public enum AuthState
{
	Connected,
	NeedsReauth,
	Error,
}

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

public enum InitialSyncMode
{
	LastNMonths,
	LastNMessages,
	Full,
}

public enum CertificateTrustMode
{
	Default,
	TrustAll,
}
