using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Domain;

/// <summary>
/// Topology discovery state, 1:1 with <see cref="Account"/> (§1).
/// </summary>
/// <remarks>
/// Folder discovery is not a by-product of message sync: Gmail's <c>history.list</c> is a
/// message-history stream and does not report labels created, renamed or deleted elsewhere.
/// Each provider reconciles topology separately.
/// </remarks>
public class MailboxTopologySyncState
{
	public Guid AccountId { get; set; }

	/// <summary>Provider-specific, where the provider offers one.</summary>
	public string? Cursor { get; set; }

	public DateTimeOffset? LastReconciledAt { get; set; }
	public string? LastError { get; set; }
}

/// <summary>
/// How much of the user's requested history has been materialised locally, 1:1 with
/// <see cref="Mailbox"/> (§1). Bounds are coverage targets, not membership limits — messages
/// discovered through another mailbox or the account change stream may also appear here.
/// </summary>
public class MailboxCoverageState
{
	public Guid MailboxId { get; set; }
	public CoverageStatus Status { get; set; }
	public int MessagesFetched { get; set; }
	public int? EstimatedTotal { get; set; }

	/// <summary>Persisted every page. This is what makes coverage resumable.</summary>
	public string? ResumeToken { get; set; }

	public DateTimeOffset? StartedAt { get; set; }
	public string? LastError { get; set; }
}

public enum CoverageStatus
{
	NotStarted,
	Backfilling,
	Covered,
	Failed,
}

/// <summary>
/// Live change tracking. Scope varies by provider: <see cref="MailboxId"/> is null for
/// Gmail, whose history sequence is account-wide. Per-label cursors would be fiction, would
/// consume the same stream repeatedly, and would race between label jobs (§1).
/// </summary>
public class ChangeStreamState
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>Null for Gmail — the stream is account-scoped. Set for Graph and IMAP.</summary>
	public Guid? MailboxId { get; set; }

	/// <summary>Discriminator for <see cref="CursorState"/>.</summary>
	public CursorKind CursorKind { get; set; }

	/// <summary>
	/// Structured and versioned per provider, not an opaque string — a single string would
	/// pretend the three are equivalent when they are not.
	/// </summary>
	public ProviderCursorState? CursorState { get; set; }

	public DateTimeOffset? BaselineEstablishedAt { get; set; }
	public DateTimeOffset? LastSyncedAt { get; set; }
	public string? LastError { get; set; }
}

/// <summary>
/// Integrity reconciliation state, 1:1 with <see cref="Mailbox"/> (§1). Universal
/// infrastructure, not an IMAP workaround, with two distinct triggers: recovery from a
/// broken cursor (something is wrong) and periodic reconciliation despite a valid cursor,
/// on degraded IMAP only (nothing is wrong).
/// </summary>
public class IntegrityReconciliationState
{
	public Guid MailboxId { get; set; }
	public DateTimeOffset? LastReconciledAt { get; set; }
	public string? LastError { get; set; }
}
