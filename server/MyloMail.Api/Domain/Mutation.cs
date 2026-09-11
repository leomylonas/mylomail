using MyloMail.Api.Errors;
using Tapper;

namespace MyloMail.Api.Domain;

/// <summary>
/// One durable user intention against one message (§6).
/// </summary>
/// <remarks>
/// <b>This record carries stable local identity only.</b> There is deliberately no provider
/// message or mailbox identifier on it, and an architecture test asserts as much: capturing
/// <c>ProviderOccurrenceId</c> at enqueue time would preserve exactly the staleness the
/// chain exists to prevent, and would leave the sequencing decorative. Provider identity is
/// resolved at execution, after preceding mutations settle.
/// <para>
/// Mailbox references are local <see cref="Mailbox.Id"/> for the same reason. A useful
/// consequence is that a folder rename invalidates no queued mutation at all — only
/// deletion does.
/// </para>
/// </remarks>
public class MutationItem
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }

	/// <summary>
	/// Ordering is keyed on the message, never on the occurrence: occurrence identity is
	/// exactly what a move invalidates, so an occurrence-keyed chain would be destroyed by
	/// the operation it exists to sequence. This also covers Gmail label add/remove pairs,
	/// which are message-scoped.
	/// </summary>
	public Guid MessageId { get; set; }

	/// <summary>
	/// Monotonically increasing per <c>(AccountId, MessageId)</c>, assigned in the same
	/// transaction as the insert. Only the lowest non-terminal sequence for a message is
	/// eligible for execution.
	/// </summary>
	public long Sequence { get; set; }

	public MutationOperationKind OperationKind { get; set; }
	public MutationState State { get; set; }

	/// <summary>Destination for <see cref="MutationOperationKind.MoveMessage"/>. Local id, never a provider id.</summary>
	public Guid? TargetMailboxId { get; set; }

	/// <summary>
	/// The membership <see cref="MutationOperationKind.RemoveFromMailbox"/> refers to. That
	/// specific membership is part of the intent: if it no longer exists the operation is
	/// unsatisfiable and is cancelled, never silently broadened to another occurrence.
	/// </summary>
	public Guid? ScopeMailboxId { get; set; }

	/// <summary>
	/// Absolute per field, not a toggle, and null means leave unchanged. Absolute values are
	/// what make a flag mutation safe to replay after an ambiguous outcome.
	/// </summary>
	public bool? DesiredIsRead { get; set; }

	/// <inheritdoc cref="DesiredIsRead"/>
	public bool? DesiredIsFlagged { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? CompletedAt { get; set; }

	/// <summary>
	/// Ownership only. A lease answers "which worker owns this?" and never "what may already
	/// have happened on the server?" — that is <see cref="MutationExecutionAttempt"/>.
	/// </summary>
	public string? LeaseOwner { get; set; }

	/// <inheritdoc cref="LeaseOwner"/>
	public DateTimeOffset? LeaseExpiresAt { get; set; }

	/// <summary>Why this item reached a terminal non-success state, for the user and for the chain re-evaluation that follows.</summary>
	public string? LastError { get; set; }

	public ErrorCategory? FailureCategory { get; set; }

	/// <summary>
	/// Reserved for genuine causal dependency that execution-time resolution cannot infer.
	/// Most ordering falls out of intent semantics without it, so this is deliberately not
	/// something every mutation declares.
	/// </summary>
	public Guid? DependsOnMutationItemId { get; set; }

	/// <summary>Terminal items are no longer eligible and no longer block the chain behind them.</summary>
	public bool IsTerminal => State is MutationState.Completed or MutationState.Failed or MutationState.Cancelled;
}

/// <summary>
/// The five operations, kept separate because they mean genuinely different things and have
/// genuinely different consequences per provider. A single <c>DeleteMessages</c> hid label
/// removal, trashing and permanent deletion behind one method (§6).
/// </summary>
public enum MutationOperationKind
{
	SetFlags,
	MoveMessage,
	RemoveFromMailbox,
	MoveToTrash,
	DeletePermanently,

	/// <summary>
	/// Not one of the five message operations: send has no <c>MutationItem</c> and is never
	/// part of a chain. It appears here only so an attempt can say what it was.
	/// </summary>
	Send,
}

public enum MutationState
{
	Pending,
	Leased,
	Completed,
	Failed,
	Cancelled,
}

/// <summary>
/// How an item may be recovered after an ambiguous outcome. Resolved from operation type
/// and the provider's negotiated capabilities (§6).
/// </summary>
public enum MutationRecoveryPolicy
{
	/// <summary>Idempotent — absolute flag sets, Gmail label add/remove. Replay freely.</summary>
	RetrySafe,

	/// <summary>Moves and permanent deletion. Re-establish current location or existence first.</summary>
	ReconcileThenRetry,

	/// <summary>Send. Never blindly replayed; reconciled against Sent by the pre-generated <c>Message-ID</c>.</summary>
	AmbiguousOutcome,
}

/// <summary>
/// A durable record that a provider call <b>may</b> have been seen (§6).
/// </summary>
/// <remarks>
/// This exists because a lease cannot answer the question it is repeatedly mistaken for. No
/// local record of an attempt is evidence about what the server actually did: the absence of
/// a persisted response never implies the operation failed remotely.
/// </remarks>
public class MutationExecutionAttempt
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public ProviderType Provider { get; set; }
	public MutationOperationKind OperationKind { get; set; }
	public MutationAttemptState State { get; set; }
	public DateTimeOffset CreatedAt { get; set; }

	/// <summary>
	/// Set when this attempt is a send. Send gets its own attempt, always, with exactly one
	/// item — which is a column here rather than a row in
	/// <see cref="MutationExecutionAttemptItem"/> precisely because "exactly one" should be
	/// true by construction rather than by convention (§6).
	/// </summary>
	public Guid? OutboxItemId { get; set; }

	/// <summary>
	/// Set synchronously and durably immediately before the irreversible provider call. A
	/// crash between that write and the call produces a false positive, which is correct: a
	/// false positive costs one unnecessary reconciliation, a false negative silently loses
	/// an outcome or duplicates an externally visible side effect.
	/// </summary>
	public DateTimeOffset? DispatchedAt { get; set; }

	public DateTimeOffset? ResultPersistedAt { get; set; }

	public ICollection<MutationExecutionAttemptItem> Items { get; set; } = [];
}

public enum MutationAttemptState
{
	Prepared,
	Dispatched,
	Completed,
	Ambiguous,
}

/// <summary>
/// Membership of an attempt. Ambiguity is per attempt, not per operation type: an item is
/// ambiguous because it belonged to a <see cref="MutationAttemptState.Dispatched"/> attempt
/// with no persisted result, so the recovery sweep queries from attempts, not item states.
/// </summary>
public class MutationExecutionAttemptItem
{
	public Guid AttemptId { get; set; }
	public Guid MutationItemId { get; set; }

	/// <summary>
	/// Stable local target selected for this dispatch. Provider identity is still resolved
	/// at call time; persisting this prevents a later special-use override from changing
	/// what an ambiguous attempt meant.
	/// </summary>
	public Guid? ResolvedTargetMailboxId { get; set; }
}

/// <summary>
/// Locally desired state, per message per field, referencing the mutation that owns it (§6).
/// </summary>
/// <remarks>
/// Kept separate from the server-known flags on <see cref="Message"/>. A single
/// <c>SyncPending</c> boolean cannot represent three concurrent pending operations, and the
/// first to complete would clear it while two remained outstanding.
/// </remarks>
public class MessagePendingChange
{
	public Guid Id { get; set; }
	public Guid MessageId { get; set; }
	public MessageFlagField Field { get; set; }
	public bool DesiredValue { get; set; }
	public Guid MutationItemId { get; set; }
}

[TranspilationSource]
public enum MessageFlagField
{
	IsRead,
	IsFlagged,
}
