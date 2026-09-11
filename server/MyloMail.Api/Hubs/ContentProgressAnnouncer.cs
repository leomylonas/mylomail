using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Hubs;

/// <summary>
/// Reports content-indexing progress as the aggregate view §1 calls for.
/// </summary>
/// <remarks>
/// Content acquisition is message state, not mailbox state — under Gmail's canonical model one
/// message belongs to several labels at once, so there is no per-mailbox indexing queue to
/// report the position of. What a mailbox can honestly say is how many of the messages it
/// currently holds have readable content, which is what this counts, for each mailbox the
/// just-indexed message belongs to.
/// </remarks>
internal static class ContentProgressAnnouncer
{
	public static async Task AnnounceAsync(
		MyloMailDbContext context,
		IHubEvents events,
		Guid messageId,
		CancellationToken ct = default
	)
	{
		var mailboxIds = await context
			.MessageMailboxes.Where(occurrence => occurrence.MessageId == messageId)
			.Select(occurrence => occurrence.MailboxId)
			.Distinct()
			.ToListAsync(ct);

		foreach (var mailboxId in mailboxIds)
		{
			var total = await context.MessageMailboxes.CountAsync(
				occurrence => occurrence.MailboxId == mailboxId,
				ct
			);
			var indexed = await context
				.MessageMailboxes.Where(occurrence => occurrence.MailboxId == mailboxId)
				.Join(
					context.MessageContentStates.Where(state => state.Status == ContentStatus.Indexed),
					occurrence => occurrence.MessageId,
					state => state.MessageId,
					(occurrence, _) => occurrence.MessageId
				)
				.CountAsync(ct);

			await events.SyncProgressAsync(
				new SyncProgressDto(mailboxId, SyncProgressKind.Content, Status: null, indexed, total)
			);
		}
	}
}
