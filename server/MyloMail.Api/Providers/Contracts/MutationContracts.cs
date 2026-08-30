using MyloMail.Api.Errors;

namespace MyloMail.Api.Providers.Contracts;

/// <summary>
/// Addresses one message occurrence for one execution attempt.
/// </summary>
/// <remarks>
/// <b>This is an ephemeral execution DTO and must never be persisted.</b> It carries
/// volatile provider identity; storing it into a mutation record reintroduces exactly the
/// staleness the mutation chain exists to prevent (§1, §6). It is constructed at execution
/// time, from stable local intent, and discarded with the attempt.
/// </remarks>
public record MessageOccurrenceRef(Guid MessageId, Guid MailboxId, string ProviderOccurrenceId);

/// <summary>
/// A partial flag update: null means leave unchanged. A mixed multi-select must not clobber
/// flags the user did not touch, which is why each field is nullable rather than the update
/// being absolute-per-message.
/// </summary>
/// <remarks>
/// <b><c>IsAnswered</c> is deliberately absent.</b> IMAP has <c>\Answered</c>, but Gmail's
/// mutation model is label-based with no <c>ANSWERED</c> system label and Graph exposes no
/// equivalent first-class mutable property. Including it would overpromise provider
/// uniformity. Answered state remains readable metadata on <see cref="MessageDto"/> (§2).
/// <para>
/// Absolute values per field, rather than toggles, are also what make flag mutations safe
/// to replay (§6).
/// </para>
/// </remarks>
public record FlagUpdate(bool? IsRead, bool? IsFlagged);

/// <summary>
/// The result of a batch mutation. Batch is the default shape, not an optimisation:
/// multi-select routinely spans hundreds of messages, and all three providers batch
/// natively. Single operations pass a one-element list (§2).
/// </summary>
public record BatchResult(IReadOnlyList<BatchItemResult> Items);

/// <summary>
/// Per-item outcome, correlated to the submitted occurrence by
/// <c>(MessageId, MailboxId)</c>. Results are genuinely per item — a batch that reports one
/// aggregate success or failure is a conformance failure, because partial success is the
/// normal case.
/// </summary>
/// <remarks>
/// <b><see cref="MailboxId"/> is an amendment to the record as written in §2</b>, which keys
/// results on <c>MessageId</c> alone. <c>RemoveFromMailbox</c> is membership-scoped (§6) and
/// a message legitimately has several occurrences under Gmail's label model (§1), so a batch
/// can carry two refs sharing a <c>MessageId</c> and their results would be
/// indistinguishable — the caller could not tell which membership succeeded.
/// <para>
/// §6's ordering rule means the mutation worker never batches two operations for one message
/// today, so this is not a live bug. It is fixed regardless because "per item" should mean an
/// item is correlatable from the contract itself, not by virtue of a scheduling invariant
/// enforced elsewhere. Both identities are local and stable, so this leaks no volatile
/// provider identity into the result.
/// </para>
/// Callers must not submit duplicate <c>(MessageId, MailboxId)</c> pairs in one batch.
/// </remarks>
public record BatchItemResult(
	Guid MessageId,
	Guid MailboxId,
	bool Succeeded,
	MutationProblemDetails? Problem,
	IReadOnlyList<OccurrenceChange> OccurrenceChanges
);

/// <summary>
/// A membership created or removed by the mutation. This is what allows a move to report
/// its destination identity — an IMAP move changes the UID, and with UIDPLUS or MOVE the
/// server returns the new one.
/// </summary>
public record OccurrenceChange(Guid MailboxId, string? NewProviderOccurrenceId, bool Removed)
{
	/// <summary>
	/// An addition whose provider id is unknown. §2 requires that this be surfaced rather
	/// than guessed at, and under an IMAP move without UIDPLUS it is the normal path, not an
	/// edge case: the occurrence is unaddressable on the server until it is reconciled.
	/// </summary>
	/// <remarks>
	/// Derived, not reported. Storing it as a separate flag would allow a change to claim no
	/// reconciliation is needed while carrying no id, and default it to that.
	/// </remarks>
	public bool RequiresDestinationReconciliation => !Removed && NewProviderOccurrenceId is null;
}
