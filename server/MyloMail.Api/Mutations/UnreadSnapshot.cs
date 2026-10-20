using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Records, in the same transaction as the change, how far a confirmed mutation has moved a
/// mailbox's unread count since the provider's snapshot was read.
/// </summary>
/// <remarks>
/// <c>MailboxUnreadProjection</c> shows the provider's snapshot projected over local state. Once
/// a pending intent is confirmed it stops being a pending delta, so the confirmed movement has to
/// be kept at the same moment, or the displayed count falls back to a figure that predates the
/// change until topology sync next reads the provider. It is kept in
/// <see cref="Domain.Mailbox.UnreadConfirmedDelta"/>, never in the provider-reported counts, which
/// only topology sync writes (AGENTS.md: the two counts stay separate); the next topology sync
/// resets it with the snapshot it takes. Every path that settles an outcome — the executor and
/// crash reconciliation — must call this, or recovery would show a different count from the
/// normal path.
/// </remarks>
internal static class UnreadSnapshot
{
	public static Task<bool> IsUnreadAsync(MyloMailDbContext context, Guid messageId, CancellationToken ct) =>
		context.Messages.AnyAsync(message => message.Id == messageId && !message.IsRead, ct);

	public static async Task AdjustAsync(MyloMailDbContext context, Guid mailboxId, int delta, CancellationToken ct)
	{
		var mailbox = await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailboxId, ct);
		if (mailbox is null)
		{
			return;
		}

		mailbox.UnreadConfirmedDelta += delta;
	}
}
