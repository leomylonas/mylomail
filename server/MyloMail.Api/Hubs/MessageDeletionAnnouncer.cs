using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Hubs;

/// <summary>
/// Raises <c>MessageDeleted</c> (§7) for canonical messages that have just lost their last
/// mailbox membership.
/// </summary>
/// <remarks>
/// <para>
/// Losing the last membership is the deletion the renderer can act on, not physical row
/// removal: a membership-less message is absent from every list and reading pane the moment
/// its last occurrence goes, while collection happens minutes later behind §6's orphan grace
/// period, and by then the event would describe nothing the UI still shows.
/// </para>
/// <para>
/// The check therefore runs against committed state, after the removing transaction. A Graph
/// move whose destination addition landed in the same page never announces a deletion, and
/// one whose addition lands on a later page re-announces the message through the ordinary
/// observed path — the opposite ordering, a removal-only page, is a real disappearance for as
/// long as it lasts.
/// </para>
/// </remarks>
internal static class MessageDeletionAnnouncer
{
	public static async Task AnnounceAsync(
		MyloMailDbContext context,
		IHubEvents events,
		IReadOnlyCollection<Guid> candidateMessageIds,
		CancellationToken ct = default
	)
	{
		if (candidateMessageIds.Count == 0)
		{
			return;
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
	}
}
