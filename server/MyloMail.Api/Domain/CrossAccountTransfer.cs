namespace MyloMail.Api.Domain;

/// <summary>
/// One message being moved to a folder of another account: copied into the target, and only then
/// removed from the source.
/// </summary>
/// <remarks>
/// <para>
/// A move across accounts is two provider operations on two servers, which no provider can make
/// atomic. The order is what keeps it safe: the copy is made first and the original is removed
/// only once the copy is known to exist, so a failure anywhere leaves the message in at least one
/// place, never none. The worst outcome of an ambiguous crash is a duplicate, and that is avoided
/// too by looking for the copy (by <c>Message-ID</c>) before ever importing again.
/// </para>
/// <para>
/// Everything here is local identity (§1): the source <see cref="Message.Id"/> and the target
/// <see cref="Mailbox.Id"/>. Provider ids are resolved when the import runs and never stored.
/// Removing the original is an ordinary <see cref="MutationItem"/> on the source account,
/// enqueued after the import is confirmed, so it gets the same ordering and recovery as any other
/// delete.
/// </para>
/// </remarks>
public class CrossAccountTransfer
{
	public Guid Id { get; set; }

	public Guid SourceAccountId { get; set; }
	public Guid MessageId { get; set; }

	public Guid TargetAccountId { get; set; }
	public Guid TargetMailboxId { get; set; }

	/// <summary>
	/// The message's own <c>Message-ID</c>, captured when the transfer was made. It is how the copy
	/// is found after a crash that left the import's outcome unknown; a message without one cannot
	/// be found, and is therefore never retried after an ambiguous attempt (§1, §6).
	/// </summary>
	public string? MessageIdHeader { get; set; }

	/// <summary>Carried over so the copy arrives with the read and starred state the original had.</summary>
	public bool IsRead { get; set; }

	public bool IsFlagged { get; set; }

	/// <summary>
	/// How many copies of <see cref="MessageIdHeader"/> the target folder already held before the first
	/// import, or null while that has not been looked at. Recovery after an unknown outcome counts the
	/// copies again and treats <i>more</i> than this as proof the import happened: a message already
	/// there with the same <c>Message-ID</c> (they are not unique) must not be mistaken for it.
	/// </summary>
	public int? BaselineCopies { get; set; }

	public CrossAccountTransferState State { get; set; }
	public int Attempts { get; set; }
	public string? LastError { get; set; }

	public DateTimeOffset CreatedAt { get; set; }

	/// <summary>When the import was last marked dispatched — the moment its outcome became uncertain.</summary>
	public DateTimeOffset? DispatchedAt { get; set; }

	/// <summary>When the copy was confirmed to exist in the target.</summary>
	public DateTimeOffset? ImportedAt { get; set; }

	public DateTimeOffset? CompletedAt { get; set; }
}

public enum CrossAccountTransferState
{
	/// <summary>Waiting for its turn; nothing has been sent to the target.</summary>
	Pending,

	/// <summary>
	/// The import may have happened. Written synchronously before the provider call (§6): a crash
	/// from here until <see cref="Imported"/> means "unknown", settled by looking for the copy.
	/// </summary>
	Dispatched,

	/// <summary>The copy exists in the target. The original has still to be removed.</summary>
	Imported,

	/// <summary>The original has been handed to the source account's mutation queue for removal.</summary>
	Completed,

	/// <summary>
	/// Given up, with <see cref="CrossAccountTransfer.LastError"/> saying why. The original is
	/// untouched; a copy may or may not exist in the target.
	/// </summary>
	Failed,
}
