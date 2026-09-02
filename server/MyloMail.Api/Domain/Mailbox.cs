namespace MyloMail.Api.Domain;

/// <summary>
/// A mailbox (§1). <see cref="Id"/> is local and canonical; provider calls always address
/// <see cref="ProviderMailboxId"/>, never <see cref="Name"/> or a derived path.
/// </summary>
public class Mailbox
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>
	/// The provider's identifier: Gmail label id, Graph folder id, IMAP full folder name.
	/// Null only for synthesised intermediate nodes in a derived Gmail label hierarchy,
	/// which have no backing provider object; any other null is a bug.
	/// </summary>
	public string? ProviderMailboxId { get; set; }

	public Guid? ParentId { get; set; }

	/// <summary>Display name — the leaf name, not a path.</summary>
	public string Name { get; set; } = string.Empty;

	public SpecialUse SpecialUse { get; set; }

	/// <summary>
	/// User-set, highest precedence over both a real server-reported RFC 6154 SPECIAL-USE
	/// attribute and the IMAP provider's own name-based fallback guess (§13 Epic 2) — for a
	/// server that doesn't advertise the extension and whose folder names the fallback
	/// doesn't recognise, or guesses wrong. Never written by sync, the same convention as
	/// <see cref="InitialSyncModeOverride"/>: an override column sync logic must not clobber.
	/// </summary>
	public SpecialUse? SpecialUseOverride { get; set; }

	public bool IsSubscribed { get; set; }

	/// <summary>Local sidebar ordering only. No provider supports arbitrary folder ordering, so this is never pushed upstream.</summary>
	public int LocalSortOrder { get; set; }

	/// <summary>Sidebar expand/collapse (§13 Epic 2). Local UI preference only, the same as
	/// <see cref="LocalSortOrder"/> — persisted so it survives a restart, never pushed upstream.</summary>
	public bool IsCollapsed { get; set; }

	public InitialSyncMode? InitialSyncModeOverride { get; set; }
	public int? InitialSyncBoundValueOverride { get; set; }

	/// <summary>
	/// Provider-reported counts, refreshed on each sync. These are what the sidebar
	/// displays — a locally computed count is wrong under bounded sync (§1).
	/// </summary>
	public int? ProviderTotalCount { get; set; }
	public int? ProviderUnreadCount { get; set; }

	/// <summary>
	/// Incremented whenever this mailbox is deleted, recreated or replaced by topology
	/// reconciliation. Work carries the generation it was issued under and is discarded on
	/// mismatch, so a late page cannot mutate a mailbox that has since been recreated (§1).
	/// </summary>
	public int TopologyGeneration { get; set; }

	public ImapMailboxMetadata? ImapMetadata { get; set; }

	/// <summary>What every caller should actually treat as this mailbox's role — the one
	/// place the override-precedence rule is expressed, rather than each call site
	/// remembering to check <see cref="SpecialUseOverride"/> first.</summary>
	public SpecialUse EffectiveSpecialUse => SpecialUseOverride ?? SpecialUse;
}

/// <summary>
/// IMAP hierarchy is a server-specific string convention, not a portable path. Keeping the
/// full name and the server-declared delimiter as provider metadata avoids deriving
/// identity from a user-facing path (§1).
/// </summary>
public class ImapMailboxMetadata
{
	public Guid MailboxId { get; set; }
	public string FullName { get; set; } = string.Empty;
	public char HierarchyDelimiter { get; set; }
	public string? NamespacePrefix { get; set; }
}
