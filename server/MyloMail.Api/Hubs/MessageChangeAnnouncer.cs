using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Hubs;

/// <summary>
/// Raises the two message-level §7 events whose trigger is a change to local state rather
/// than a provider page: <c>MessageDeleted</c> for a message that has lost its last mailbox
/// membership, and <c>MessageUpdated</c> for one a completed job has just confirmed.
/// </summary>
/// <remarks>
/// <para>
/// Losing the last membership is the deletion the renderer can act on, not physical row
/// removal: a membership-less message is absent from every list and reading pane the moment
/// its last occurrence goes, while collection happens minutes later behind §6's orphan grace
/// period. <see cref="Scheduling.TombstoneGcJobs"/> announces that collection separately,
/// because it is the point at which <c>GetMessageBody</c> starts reporting the message gone.
/// </para>
/// <para>
/// Both checks run against committed state, after the writing transaction. A Graph move whose
/// destination addition landed in the same page never announces a deletion, and one whose
/// addition lands on a later page re-announces the message through the ordinary observed
/// path — the opposite ordering, a removal-only page, is a real disappearance for as long as
/// it lasts.
/// </para>
/// </remarks>
internal static class MessageChangeAnnouncer
{
	/// <returns>The message ids announced, so a caller can skip updating what it just deleted.</returns>
	public static async Task<IReadOnlyList<Guid>> AnnounceDeletedAsync(
		MyloMailDbContext context,
		IHubEvents events,
		IReadOnlyCollection<Guid> candidateMessageIds,
		CancellationToken ct = default
	)
	{
		if (candidateMessageIds.Count == 0)
		{
			return [];
		}

		var candidates = candidateMessageIds.Distinct().ToArray();
		var deleted = await context
			.Messages.Where(message =>
				candidates.Contains(message.Id)
				&& !context.MessageMailboxes.Any(occurrence => occurrence.MessageId == message.Id)
			)
			.Select(message => message.Id)
			.ToListAsync(ct);

		foreach (var messageId in deleted)
		{
			await events.MessageDeletedAsync(messageId);
		}

		return deleted;
	}

	/// <summary>
	/// Announces messages whose server-known and pending state a completed local job has just
	/// settled (§7). Every window holds its own optimistic projection, so a confirmation only
	/// the acting window hears about leaves the others showing a pending badge indefinitely.
	/// </summary>
	public static async Task AnnounceUpdatedAsync(
		MyloMailDbContext context,
		IHubEvents events,
		IReadOnlyCollection<Guid> messageIds,
		CancellationToken ct = default
	)
	{
		if (messageIds.Count == 0)
		{
			return;
		}

		var ids = messageIds.Distinct().ToArray();
		var messages = await context.Messages.Where(message => ids.Contains(message.Id)).ToListAsync(ct);
		foreach (var message in messages)
		{
			await events.MessageUpdatedAsync(MessageEventMapper.ToSummary(message));
		}
	}
}
