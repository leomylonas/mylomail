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
/// Per-item outcome. Results are genuinely per item — a batch that reports one aggregate
/// success or failure is a conformance failure, because partial success is the normal case.
/// </summary>
public record BatchItemResult(
	Guid MessageId,
	bool Succeeded,
	MutationProblemDetails? Problem,
	IReadOnlyList<OccurrenceChange> OccurrenceChanges
);

/// <summary>
/// A membership created or removed by the mutation. This is what allows a move to report
/// its destination identity — an IMAP move changes the UID, and with UIDPLUS or MOVE the
/// server returns the new one.
/// </summary>
/// <remarks>
/// Where the server does not report the destination id,
/// <see cref="NewProviderOccurrenceId"/> is null and
/// <see cref="RequiresDestinationReconciliation"/> is set, so the caller reconciles rather
/// than guessing (§2).
/// </remarks>
public record OccurrenceChange(
	Guid MailboxId,
	string? NewProviderOccurrenceId,
	bool Removed,
	bool RequiresDestinationReconciliation = false
);
