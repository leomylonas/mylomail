using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Mutations;

/// <summary>
/// Keeps a mailbox's provider unread snapshot consistent with a change a confirmed mutation just
/// made to the server-known state, in the same transaction as that change.
/// </summary>
/// <remarks>
/// <c>MailboxUnreadProjection</c> shows the provider's snapshot projected over local state. Once
/// a pending intent is confirmed it stops being a pending delta, so the snapshot has to move with
/// the server-known flag or membership at the same moment, or the displayed count falls back to
/// a figure that predates the change until topology sync next reads the provider. The snapshot
/// and its local baseline move by exactly the same amount and are not clamped: the displayed
/// figure depends on their difference, and clamping one at zero would destroy it. The next
/// topology sync overwrites both with the provider's own numbers. Every path that settles an
/// outcome — the executor and crash reconciliation — must call this, or recovery would show a
/// different count from the normal path.
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

		if (mailbox.ProviderUnreadCount is { } snapshot)
		{
			mailbox.ProviderUnreadCount = snapshot + delta;
		}

		if (mailbox.UnreadAtProviderCount is { } baseline)
		{
			mailbox.UnreadAtProviderCount = baseline + delta;
		}
	}
}
