using MyloMail.Api.Domain;
using MyloMail.Api.Errors;

namespace MyloMail.Api.Providers.Contracts;

/// <summary>
/// The outcome of authenticating an account. A failure is categorised, never a flat string:
/// an <see cref="ErrorCategory.Auth"/> result is what sets <c>AuthState.NeedsReauth</c> and
/// pauses the account's jobs (§3).
/// </summary>
public record AuthResult(bool Succeeded, AuthState State, MutationProblemDetails? Problem);

/// <summary>
/// A mailbox as the provider reports it. Parenting is expressed by provider id rather than
/// local <c>Guid</c>, because the caller resolves topology into local identity.
/// </summary>
/// <remarks>
/// Gmail labels are flat with no parent ids; any tree is derived by splitting label names
/// on <c>/</c> and synthesising intermediate rows, so a Gmail provider reports
/// <see cref="ParentProviderMailboxId"/> as null and lets the caller derive nesting (§1).
/// </remarks>
public record MailboxDto
{
	public required string ProviderMailboxId { get; init; }
	public required string Name { get; init; }
	public string? ParentProviderMailboxId { get; init; }
	public SpecialUse SpecialUse { get; init; }
	public bool IsSubscribed { get; init; }

	/// <summary>Provider-reported counts, null where the provider cannot report them.</summary>
	public int? TotalCount { get; init; }
	public int? UnreadCount { get; init; }

	/// <summary>Populated by IMAP only.</summary>
	public ImapMailboxMetadataDto? ImapMetadata { get; init; }
}

/// <summary>
/// The server's full hierarchical name plus its declared delimiter. The delimiter varies by
/// server and must not be assumed (§1).
/// </summary>
/// <remarks>
/// <paramref name="HierarchyDelimiter"/> is a single character carried as a string:
/// <c>char</c> has no TypeScript equivalent and generates an unusable schema. The domain
/// entity keeps <c>char</c>.
/// </remarks>
public record ImapMailboxMetadataDto(
	string FullName,
	string HierarchyDelimiter,
	string? NamespacePrefix
);

/// <summary>
/// The original RFC 5322 bytes. This is the single stored representation of content —
/// reconstructing MIME from parsed parts produces a plausible message rather than the
/// message, losing DKIM signatures, original headers and encrypted parts (§1).
/// </summary>
/// <remarks>
/// Content acquisition is one raw fetch per message: body, headers, attachment metadata and
/// search content are all parsed from this. There is no separate body or attachment fetch.
/// </remarks>
public record RawMessageResult(byte[] RawBytes);

/// <summary>
/// Attachment limits, reported honestly rather than as a single number. Exchange
/// message-size limits are configured per tenant and are not reliably discoverable, so
/// <see cref="IsUnknown"/> is explicit rather than a false precise value (§15).
/// </summary>
/// <remarks>
/// Callers check against base64 overhead on total message size, not just per-file raw size.
/// Where the limit is unknown the user is warned, never blocked.
/// </remarks>
public record AttachmentConstraints(
	long? ApiPerFileLimit,
	long? KnownMessageSizeLimit,
	long? ConfiguredOverride,
	bool IsUnknown
);

/// <summary>
/// The server-side id and revision after creating or updating a draft. The revision is what
/// the next update passes as its expected value, making detect-don't-merge enforceable (§1).
/// </summary>
public record DraftResult(string ProviderDraftId, string? ProviderRevision, string? ProviderMessageId = null);
