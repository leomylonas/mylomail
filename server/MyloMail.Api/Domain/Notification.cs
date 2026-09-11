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

	/// <summary>
	/// Null when this notification was created from a staged, not-yet-replayed change-stream
	/// page (§3, §13 Epic 9): the canonical row does not exist yet at that point, and creating
	/// one early would be exactly the "deferred behind canonical replay" this exists to avoid.
	/// Backfilled once replay resolves <see cref="ProviderStableId"/> to a local message.
	/// </summary>
	public Guid? MessageId { get; set; }

	/// <summary>
	/// The provider's own stable message id — set only for a notification recorded from
	/// staging, where it is the sole identity available until replay runs. Gmail always has
	/// one; this path exists only for Gmail's account-scoped staged history.
	/// </summary>
	public string? ProviderStableId { get; set; }

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

/// <summary>Whether a notification click can navigate now or is waiting for staged replay.</summary>
[TranspilationSource]
public enum NotificationNavigationStatus
{
	Pending,
	Ready,
}
