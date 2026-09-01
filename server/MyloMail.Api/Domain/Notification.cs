using Tapper;

namespace MyloMail.Api.Domain;

/// <summary>
/// A durable record of one OS notification this account owes the user (§13 Epic 9).
/// </summary>
/// <remarks>
/// Delivery is at-least-once by explicit policy: the SQLite transaction that creates this row
/// covers the database, but nothing can make it cover the native OS call too. The row is
/// created transactionally alongside the message that earned it, dispatch reads pending rows
/// and asks the shell to show them, and <see cref="DeliveredAt"/> is set afterwards. A crash
/// between dispatch and that update redelivers the same notification — an occasional duplicate
/// is the accepted cost of never silently dropping one.
/// </remarks>
public class NotificationRecord
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public Guid MessageId { get; set; }
	public NotificationKind Kind { get; set; }
	public DateTimeOffset CreatedAt { get; set; }

	/// <summary>Null until the shell has been asked to show it at least once.</summary>
	public DateTimeOffset? DeliveredAt { get; set; }
}

[TranspilationSource]
public enum NotificationKind
{
	NewMessage,
}
