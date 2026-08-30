namespace MyloMail.Api.Domain;

/// <summary>
/// A message queued for sending (§15).
/// </summary>
/// <remarks>
/// Undo-send and scheduled send are the same mechanism: true recall is not possible across
/// providers, so "undo" is a client-side delay before dispatch rather than anything the
/// server participates in.
/// </remarks>
public class OutboxItem
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public Guid DraftId { get; set; }

	public OutboxStatus Status { get; set; }

	/// <summary><c>now + UndoSendDelaySeconds</c> for a normal send, or an arbitrary future time.</summary>
	public DateTimeOffset ScheduledSendAt { get; set; }

	/// <summary>
	/// A stable RFC 5322 <c>Message-ID</c>, generated <b>before the first attempt</b> and
	/// reused across every retry.
	/// </summary>
	/// <remarks>
	/// This is what makes reconciliation possible after an ambiguous outcome: it is the only
	/// identifier that exists on both sides of a send whose result was never observed, so it
	/// is what the Sent mailbox is searched for. Generating it per attempt would leave a
	/// crashed send unidentifiable and a duplicate undetectable.
	/// </remarks>
	public string StableMessageId { get; set; } = string.Empty;

	public int Attempts { get; set; }
	public string? LastError { get; set; }
	public DateTimeOffset CreatedAt { get; set; }
	public DateTimeOffset? SentAt { get; set; }

	/// <summary>When reconciliation of an ambiguous send began, bounding how long it may run.</summary>
	public DateTimeOffset? ReconcilingSince { get; set; }
}

/// <summary>
/// Outbox status. Transitions are compare-and-swap, never a check followed by a write (§15).
/// </summary>
/// <remarks>
/// Cancel attempts <see cref="Scheduled"/>/<see cref="Pending"/> → <see cref="Cancelled"/>
/// while the worker attempts the same states → <see cref="Sending"/>. Exactly one wins.
/// Once <see cref="Sending"/>, cancellation reports "too late" and must not revert: a
/// recheck immediately before dispatch narrows that window but cannot close it.
/// </remarks>
public enum OutboxStatus
{
	Draft,
	Scheduled,

	/// <summary>
	/// Due and about to be dispatched. Nothing sets this yet — <see cref="Scheduled"/> items
	/// become <see cref="Sending"/> directly — but it is part of the status set §15 defines,
	/// and both cancel and claim already treat it as takeable, so introducing it later does
	/// not reopen the race.
	/// </summary>
	Pending,
	Sending,
	Sent,
	Failed,

	/// <summary>
	/// Dispatched with no observed result. <b>Never auto-retried</b> — its side effect is
	/// externally visible in a way no other operation's is, so a blind replay risks sending
	/// the message twice.
	/// </summary>
	AmbiguousOutcome,

	Cancelled,
}
