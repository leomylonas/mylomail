namespace MyloMail.Api.Domain;

/// <summary>
/// A message (§1). <see cref="Id"/> is the canonical identity: local, permanent, and never
/// derived from provider ids or headers, none of which are dependable across all three
/// providers.
/// </summary>
public class Message
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>
	/// Gmail message id, or a Graph <b>immutable</b> id (§2). Null for IMAP, which has no
	/// account-wide stable message identifier.
	/// </summary>
	public string? ProviderStableId { get; set; }

	/// <summary>
	/// RFC 5322 <c>Message-ID</c>. Nullable metadata, not identity — the RFC says SHOULD,
	/// not MUST, and duplicates occur in practice. Used for reconciliation, reply
	/// construction and future threading.
	/// </summary>
	public string? MessageIdHeader { get; set; }

	public string? InReplyToHeader { get; set; }
	public string? ReferencesHeader { get; set; }

	/// <summary>Reply routing uses this in preference to <see cref="From"/> when present.</summary>
	public IReadOnlyList<Address> ReplyToAddresses { get; set; } = [];

	/// <summary>RFC 5322 <c>Sender</c>, where it differs from <see cref="From"/>.</summary>
	public Address? SenderAddress { get; set; }

	/// <summary>Effective conversation id: provider-native where offered, otherwise RFC fallback.</summary>
	public string? ThreadId { get; set; }

	/// <summary>Whether <see cref="ThreadId"/> came from the provider rather than fallback inference.</summary>
	public bool HasProviderThreadId { get; set; }

	public IReadOnlyList<Address> From { get; set; } = [];
	public IReadOnlyList<Address> To { get; set; } = [];
	public IReadOnlyList<Address> Cc { get; set; } = [];
	public IReadOnlyList<Address> Bcc { get; set; } = [];

	public string Subject { get; set; } = string.Empty;
	public string Snippet { get; set; } = string.Empty;
	public DateTimeOffset ReceivedAt { get; set; }

	/// <summary>
	/// Server-known state. Locally desired changes live in the pending-change model (§6),
	/// never here — these three fields are what the server last told us.
	/// </summary>
	public bool IsRead { get; set; }

	/// <inheritdoc cref="IsRead"/>
	public bool IsFlagged { get; set; }

	/// <inheritdoc cref="IsRead"/>
	public bool IsDraft { get; set; }

	/// <summary>Read-only, provider-derived. Not portably mutable — see §2.</summary>
	public bool IsAnswered { get; set; }

	/// <summary>
	/// Deliberately not <c>HasAttachments</c>: inline signature images are MIME
	/// attachments, so a naive flag would show a paperclip on almost every corporate email.
	/// </summary>
	public bool HasNonInlineAttachments { get; set; }

	public long? SizeEstimate { get; set; }

	/// <summary>Whether <see cref="MessageRaw"/> has been populated.</summary>
	public bool RawFetched { get; set; }

	/// <summary>
	/// When tombstone collection first observed this message with no mailbox membership at
	/// all. Null while it has at least one, and cleared the moment it gains one again (§6).
	/// </summary>
	/// <remarks>
	/// Exists only so collection can wait out §3's Graph-move race — a source-removal delta
	/// and its matching destination-addition delta can arrive minutes apart, in either order,
	/// leaving a canonical message transiently membership-less. Recorded the first time
	/// collection notices, not the moment membership actually dropped, which only ever makes
	/// the wait longer than the race requires, never shorter.
	/// </remarks>
	public DateTimeOffset? OrphanedAt { get; set; }

	public ICollection<MessageMailbox> Occurrences { get; set; } = [];
}

/// <summary>
/// A message's membership of one mailbox (§1). A message belongs to zero or more mailboxes:
/// Gmail's canonical label model means several at once, and under Graph's folder-scoped
/// delta a move can leave a message transiently with none.
/// </summary>
public class MessageMailbox
{
	/// <summary>
	/// Local occurrence identity only. Durable mutations never reference it — execution
	/// resolves the occurrence from stable intent (§6).
	/// </summary>
	public Guid Id { get; set; }

	public Guid MessageId { get; set; }
	public Guid MailboxId { get; set; }

	/// <summary>
	/// The provider's id for <b>this message in this mailbox</b>: IMAP UID, Gmail message
	/// id, Graph immutable id. It lives here rather than on <see cref="Message"/> because
	/// IMAP UIDs are folder-scoped and change when a message moves.
	/// </summary>
	public string ProviderOccurrenceId { get; set; } = string.Empty;

	/// <summary>Per-message mod-sequence, IMAP CONDSTORE only (§3).</summary>
	public long? ImapModSeq { get; set; }
}
