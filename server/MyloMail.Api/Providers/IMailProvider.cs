using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers;

/// <summary>
/// One interface over providers that genuinely differ on identity, hierarchy, deletion and
/// change tracking. The interface absorbs those differences; callers never branch on the
/// provider. Differences that cannot be absorbed surface through
/// <see cref="Capabilities"/> rather than through provider-specific call sites (§2).
/// </summary>
/// <remarks>
/// Implementations: <c>ImapMailProvider</c> (MailKit), <c>GmailMailProvider</c>
/// (Google.Apis.Gmail.v1), <c>GraphMailProvider</c> (Microsoft.Graph). Each translates its
/// native incremental-sync mechanism — UID ranges, <c>historyId</c>, <c>deltaLink</c> —
/// into the common <see cref="SyncResult"/> shape.
/// <para>
/// Every provider must satisfy the shared conformance suite, including all three IMAP
/// capability tiers.
/// </para>
/// </remarks>
public interface IMailProvider
{
	ProviderType Type { get; }

	/// <summary>
	/// What this provider can honestly do. Drives recovery policy and the IMAP sync tier
	/// (§3, §6).
	/// </summary>
	ProviderCapabilities Capabilities { get; }

	Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct);

	/// <summary>
	/// Topology discovery is not a by-product of message sync — Gmail's history stream is a
	/// message stream and does not report labels created, renamed or deleted elsewhere (§1).
	/// </summary>
	Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(Account account, CancellationToken ct);

	Task<int> EstimateMailboxCountAsync(Account account, Mailbox mailbox, CancellationToken ct);

	/// <summary>Historical backfill, one page at a time, resumable via the returned token.</summary>
	Task<InitialSyncPage> InitialSyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		string? resumeToken,
		InitialSyncMode mode,
		int? bound,
		int pageSize,
		CancellationToken ct
	);

	/// <summary>
	/// Incremental change since <paramref name="cursor"/>.
	/// </summary>
	/// <exception cref="ProviderCursorInvalidException">
	/// The cursor can no longer be used and a baseline must be re-established (§3).
	/// </exception>
	Task<SyncResult> SyncMailboxAsync(
		Account account,
		Mailbox mailbox,
		ProviderCursorState? cursor,
		CancellationToken ct
	);

	/// <summary>
	/// Content acquisition is one raw fetch per message (§1). Body, headers, attachment
	/// metadata and search content are all parsed from this; there is no separate body or
	/// attachment fetch. Gmail <c>format=RAW</c>, Graph <c>$value</c>, IMAP
	/// <c>BODY.PEEK[]</c>.
	/// </summary>
	Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct
	);

	Task<AttachmentConstraints> GetAttachmentConstraintsAsync(Account account, CancellationToken ct);

	// Mutations — operate on occurrences and stable intent, never on captured provider ids.
	// Provider identifiers are resolved by the caller at execution time (§6).

	Task<BatchResult> SetFlagsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	);

	Task<BatchResult> MoveMessagesAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		Mailbox target,
		CancellationToken ct
	);

	/// <summary>
	/// Removes the occurrence from one mailbox without deleting the message. Kept separate
	/// from <see cref="MoveToTrashAsync"/> and <see cref="DeletePermanentlyAsync"/> because
	/// the three mean different things and collapsing them loses user intent (§2).
	/// </summary>
	Task<BatchResult> RemoveFromMailboxAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	);

	Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	);

	Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	);

	/// <summary>
	/// Sends a draft. <paramref name="stableMessageId"/> is generated before the first
	/// attempt and reused across retries, which is what makes reconciliation against the
	/// Sent mailbox possible after an ambiguous outcome (§15).
	/// </summary>
	/// <remarks>
	/// Attachment upload mechanics are absorbed entirely inside the implementation — Graph's
	/// chunked upload-session flow above ~3MB included. The signature and every caller stay
	/// identical across providers.
	/// </remarks>
	Task SendAsync(Account account, Draft draft, string stableMessageId, CancellationToken ct);

	/// <summary>
	/// Creates or updates a server-side draft. <paramref name="expectedRevision"/> is null
	/// when creating; otherwise it is passed as a precondition where the provider supports
	/// one, and a precondition failure surfaces as <c>ErrorCategory.Conflict</c> rather than
	/// an overwrite (§1, §15).
	/// </summary>
	Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	);

	Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct);

	Task<MailboxDto> CreateMailboxAsync(
		Account account,
		string name,
		Mailbox? parent,
		CancellationToken ct
	);

	Task RenameMailboxAsync(Account account, Mailbox mailbox, string newName, CancellationToken ct);

	Task MoveMailboxAsync(Account account, Mailbox mailbox, Mailbox? newParent, CancellationToken ct);

	/// <summary>
	/// Deletion is provider-divergent and the divergence must be surfaced, not hidden:
	/// deleting an IMAP folder or Graph mail folder deletes the messages in it, whereas
	/// deleting a Gmail label leaves the messages in All Mail. Callers read
	/// <see cref="ProviderCapabilities.DeletingMailboxDeletesMessages"/> to state the actual
	/// outcome (§2).
	/// </summary>
	Task DeleteMailboxAsync(Account account, Mailbox mailbox, CancellationToken ct);
}

/// <summary>
/// Resolves the implementation for a provider type. The orchestrator and hub layers never
/// contain provider-specific logic (§2).
/// </summary>
public interface IMailProviderFactory
{
	IMailProvider For(ProviderType type);
}
