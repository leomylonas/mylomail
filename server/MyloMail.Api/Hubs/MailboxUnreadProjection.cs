using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Hubs;

/// <summary>
/// The unread count the sidebar shows, as a pure function of durable state — never a cached
/// number something has to remember to keep up to date.
/// </summary>
/// <remarks>
/// <see cref="Mailbox.ProviderUnreadCount"/> is a snapshot, refreshed only when topology sync
/// next reads it, so showing it directly lagged every local change: marking a message read,
/// emptying Trash, new mail arriving. This projects it forward instead.
/// <list type="number">
/// <item><b>Complete local copy</b> (<see cref="InitialSyncMode.Full"/> and
/// <see cref="CoverageStatus.Covered"/>): every message is held, so the count is the local
/// count — exact, immediate, and independent of the provider's snapshot.</item>
/// <item><b>Bounded, covered</b>: the provider's snapshot plus how far the local unread count
/// has moved since the snapshot was taken (<see cref="Mailbox.UnreadAtProviderCount"/>). Every
/// local change, including one a mutation has just confirmed, shifts the display by exactly its
/// own size.</item>
/// <item><b>Otherwise</b> (backfilling, or no baseline): the snapshot plus the net effect of
/// intents still pending, because backfill adds old messages locally that never changed the
/// provider's count. <c>MutationExecutor</c> moves the snapshot and baseline together when it
/// confirms a read change, removal or arrival, so a confirmed change does not fall back to a
/// stale snapshot.</item>
/// </list>
/// A Drafts mailbox is always the third case: drafts never materialise as <see cref="Message"/>
/// rows (§1), so the local relation cannot represent what the provider counts there.
/// Pending intents (<see cref="MessagePendingChange"/> and in-flight removals) are always part of
/// the local figure, so an action shows the moment it is recorded, and an outcome the provider
/// has not yet echoed cannot make the count jump back. The next topology sync replaces the
/// snapshot with the provider's own figure, which remains the authority where it is the only
/// one (§1).
/// </remarks>
internal static class MailboxUnreadProjection
{
	private static readonly MutationOperationKind[] RemovalKinds =
	[
		MutationOperationKind.DeletePermanently,
		MutationOperationKind.MoveToTrash,
		MutationOperationKind.RemoveFromMailbox,
	];

	public static int Display(
		bool completeLocalCopy,
		bool covered,
		int? providerUnread,
		int? unreadAtProviderCount,
		int serverKnownUnread,
		int pendingDelta
	)
	{
		var effectiveLocal = serverKnownUnread + pendingDelta;
		var shown = completeLocalCopy || providerUnread is null
			? effectiveLocal
			: covered && unreadAtProviderCount is { } baseline
				? providerUnread.Value + (effectiveLocal - baseline)
				: providerUnread.Value + pendingDelta;
		return Math.Max(shown, 0);
	}

	/// <summary>Unread by the server-known flag only, per mailbox. Mailboxes with none are absent.</summary>
	public static async Task<Dictionary<Guid, int>> ServerKnownUnreadAsync(
		MyloMailDbContext context,
		IReadOnlyCollection<Guid> mailboxIds,
		CancellationToken ct
	) =>
		await context
			.MessageMailboxes.Where(occurrence => mailboxIds.Contains(occurrence.MailboxId))
			.Join(
				context.Messages.Where(message => !message.IsRead),
				occurrence => occurrence.MessageId,
				message => message.Id,
				(occurrence, _) => occurrence.MailboxId
			)
			.GroupBy(mailboxId => mailboxId)
			.Select(group => new { MailboxId = group.Key, Count = group.Count() })
			.ToDictionaryAsync(row => row.MailboxId, row => row.Count, ct);

	/// <summary>
	/// How far pending intents move each mailbox's unread count away from the server-known one:
	/// a pending read/unread change, or a message on its way out (trash, delete, label removal).
	/// A move is deliberately absent: its source occurrence is only resolved at execution, and a
	/// message carrying several Gmail labels would otherwise be dropped from all of them. A move
	/// counts when the executor confirms it. Mailboxes with no pending effect are absent.
	/// </summary>
	public static async Task<Dictionary<Guid, int>> PendingDeltaAsync(
		MyloMailDbContext context,
		IReadOnlyCollection<Guid> mailboxIds,
		CancellationToken ct
	)
	{
		var reads = await (
			from change in context.MessagePendingChanges
			where change.Field == MessageFlagField.IsRead
			join occurrence in context.MessageMailboxes on change.MessageId equals occurrence.MessageId
			join message in context.Messages on change.MessageId equals message.Id
			where mailboxIds.Contains(occurrence.MailboxId)
			select new
			{
				occurrence.MailboxId,
				change.MessageId,
				ServerRead = message.IsRead,
				Desired = change.DesiredValue,
			}
		).ToListAsync(ct);

		var removals = await (
			from item in context.MutationItems
			where
				(item.State == MutationState.Pending || item.State == MutationState.Leased)
				&& RemovalKinds.Contains(item.OperationKind)
			join occurrence in context.MessageMailboxes on item.MessageId equals occurrence.MessageId
			join message in context.Messages on item.MessageId equals message.Id
			where
				mailboxIds.Contains(occurrence.MailboxId)
				&& (
					item.OperationKind == MutationOperationKind.RemoveFromMailbox
						? item.ScopeMailboxId == occurrence.MailboxId
						// Message-scoped, but only settled for the occurrences the provider reports
						// (Gmail resolves one source label), so a message held in several mailboxes
						// is not guessed at: it counts when the confirmation lands.
						: context.MessageMailboxes.Count(other => other.MessageId == item.MessageId) == 1
				)
			select new
			{
				occurrence.MailboxId,
				item.MessageId,
				ServerRead = message.IsRead,
			}
		).ToListAsync(ct);

		var removed = removals.Select(row => (row.MailboxId, row.MessageId)).ToHashSet();
		var desired = reads.ToDictionary(row => (row.MailboxId, row.MessageId), row => row.Desired);
		var serverRead = reads
			.Select(row => (Key: (row.MailboxId, row.MessageId), row.ServerRead))
			.Concat(removals.Select(row => (Key: (row.MailboxId, row.MessageId), row.ServerRead)))
			.GroupBy(row => row.Key)
			.ToDictionary(group => group.Key, group => group.First().ServerRead);

		var delta = new Dictionary<Guid, int>();
		foreach (var (key, read) in serverRead)
		{
			var effectivelyUnread = !removed.Contains(key)
				&& !(desired.TryGetValue(key, out var wanted) ? wanted : read);
			var change = (effectivelyUnread ? 1 : 0) - (read ? 0 : 1);
			if (change != 0)
			{
				delta[key.MailboxId] = delta.GetValueOrDefault(key.MailboxId) + change;
			}
		}

		return delta;
	}
}
