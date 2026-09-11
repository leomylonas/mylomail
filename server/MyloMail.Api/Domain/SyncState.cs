using MyloMail.Api.Providers.Contracts;
using Tapper;

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

[TranspilationSource]
public enum CoverageStatus
{
	NotStarted,
	Backfilling,
	Covered,
	Failed,
}

/// <summary>
/// Which paged job a <see cref="Contracts.SyncProgressDto"/> came from (§7).
/// </summary>
/// <remarks>
/// Coverage finishes; content indexing keeps going afterwards, one message at a time. Without
/// this the second producer's counts would land on the first's cache entry and a completed
/// backfill would appear to restart.
/// </remarks>
[TranspilationSource]
public enum SyncProgressKind
{
	Coverage,
	Content,
}

/// <summary>
/// Whether a mailbox can currently serve its cached view. Orthogonal to
/// <see cref="CoverageStatus"/>: a backfill in progress is usable, while a completed cache
/// can be degraded by a sync or authentication fault.
/// </summary>
[TranspilationSource]
public enum MailboxAvailability
{
	Usable,
	Degraded,
	Unavailable,
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

	/// <summary>
	/// A cursor invalidation has fenced coverage but its replacement baseline has not committed.
	/// Startup must complete this state before allowing another coverage page.
	/// </summary>
	public bool IsRebasing { get; set; }
	public DateTimeOffset? LastSyncedAt { get; set; }
	public string? LastError { get; set; }

	/// <summary>
	/// This stream's notification cutoff (§13 Epic 9) — a fixed instant, captured once when
	/// the stream is first created and again immediately on a triggered resync, before
	/// resynchronisation begins. Deliberately not derived from
	/// <see cref="BaselineEstablishedAt"/>, which instead means "this stream has completed a
	/// confirmed page" and transitions null→set only after catch-up work runs: gating
	/// notifications on that would classify mail arriving during a resync's own catch-up
	/// window as predating it, which is exactly the silent drop §13 forbids.
	/// </summary>
	public DateTimeOffset NotificationBaselineAt { get; set; }
}

/// <summary>
/// One page of live change, drained durably but not yet applied to the canonical model (§3).
/// </summary>
/// <remarks>
/// This exists for Gmail's ordering problem. The account-wide history stream and per-mailbox
/// backfill can observe the same message in different states: baseline captured, backfill
/// lists a message in the Inbox, another client archives it, history records the removal,
/// and the backfill then writes its earlier view — resurrecting a membership that no longer
/// exists. Draining history durably but unapplied while backfill runs prevents
/// <c>historyId</c> expiry during a long full sync without admitting a concurrent writer to
/// the canonical model.
/// <para>
/// <see cref="Ordinal"/> preserves the order the events were observed in, which is the only
/// order it is safe to replay them in.
/// </para>
/// </remarks>
public class StagedChangeEvent
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>Monotonic per account, in observation order.</summary>
	public long Ordinal { get; set; }

	/// <summary>The serialised <c>SyncResult</c> payload, replayed verbatim.</summary>
	public string Payload { get; set; } = string.Empty;

	public DateTimeOffset StagedAt { get; set; }

	/// <summary>
	/// Notifications are derived from staged events before canonical replay, so live mail is
	/// not silently unnotified for the hours a large backfill takes (§3). This records that
	/// the scan has seen the event, independently of whether it has been applied.
	/// </summary>
	public bool ScannedForNotifications { get; set; }
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
