using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Contracts;

/// <summary>
/// A message as the provider reports it. Carries no local identity — matching this to an
/// existing <c>Message</c> is the caller's job, using the precedence in §1.
/// </summary>
public record MessageDto
{
	/// <summary>
	/// Gmail message id, or a Graph immutable id. Null for IMAP, which has no account-wide
	/// stable message identifier.
	/// </summary>
	public string? ProviderStableId { get; init; }

	/// <summary>
	/// Where this message appears. A message belongs to zero or more mailboxes — Gmail's
	/// label model means several at once — so this is a list for all three providers rather
	/// than being correct for one and bent for the others (§1).
	/// </summary>
	public required IReadOnlyList<MessageOccurrenceDto> Occurrences { get; init; }

	/// <summary>
	/// RFC 5322 <c>Message-ID</c>. Nullable metadata, never identity: the RFC says a message
	/// SHOULD have one, not MUST, and duplicates occur in practice.
	/// </summary>
	public string? MessageIdHeader { get; init; }

	public string? InReplyToHeader { get; init; }
	public string? ReferencesHeader { get; init; }

	/// <summary>Reply routing uses this in preference to <see cref="From"/> when present.</summary>
	public IReadOnlyList<Address> ReplyToAddresses { get; init; } = [];

	/// <summary>RFC 5322 <c>Sender</c>, where it differs from <see cref="From"/>.</summary>
	public Address? SenderAddress { get; init; }

	public string? ThreadId { get; init; }

	public IReadOnlyList<Address> From { get; init; } = [];
	public IReadOnlyList<Address> To { get; init; } = [];
	public IReadOnlyList<Address> Cc { get; init; } = [];
	public IReadOnlyList<Address> Bcc { get; init; } = [];

	public string Subject { get; init; } = string.Empty;
	public string Snippet { get; init; } = string.Empty;
	public required DateTimeOffset ReceivedAt { get; init; }

	public bool IsRead { get; init; }
	public bool IsFlagged { get; init; }
	public bool IsDraft { get; init; }

	/// <summary>Read-only provider-derived metadata. Not portably mutable — see <see cref="FlagUpdate"/>.</summary>
	public bool IsAnswered { get; init; }

	/// <summary>
	/// Deliberately not <c>HasAttachments</c>: inline signature images are MIME
	/// attachments, so a naive flag would show a paperclip on almost every corporate email.
	/// </summary>
	public bool HasNonInlineAttachments { get; init; }

	public long? SizeEstimate { get; init; }
}

/// <summary>
/// One message's membership of one mailbox, as the provider reports it.
/// </summary>
/// <remarks>
/// The provider id lives here rather than on the message because IMAP UIDs are scoped to a
/// folder and change when a message moves. For Gmail and Graph this repeats the stable id
/// on each occurrence, which keeps one code path (§1).
/// </remarks>
public record MessageOccurrenceDto(
	string ProviderMailboxId,
	string ProviderOccurrenceId,
	long? ImapModSeq = null
);
