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
/// just-indexed (or just-abandoned) message belongs to. Permanently failed messages are left
/// out of the total: they are no longer pending.
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
			// A message that has permanently failed will never be indexed, so counting it would
			// leave "12 of 14" on screen forever. It is not pending work.
			var failed = context
				.MessageContentStates.Where(state => state.Status == ContentStatus.Failed)
				.Select(state => state.MessageId);
			var total = await context.MessageMailboxes.CountAsync(
				occurrence => occurrence.MailboxId == mailboxId && !failed.Contains(occurrence.MessageId),
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
