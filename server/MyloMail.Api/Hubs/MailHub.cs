using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using TypedSignalR.Client;

namespace MyloMail.Api.Hubs;

/// <summary>
/// The renderer's connection to the backend (§7).
/// </summary>
/// <remarks>
/// <para>
/// Mutations here only <b>enqueue</b>. They return once the intent is durably recorded, not
/// once the provider has agreed, because the mutation chain owns execution and its outcome
/// arrives later as an event. A hub method that waited for the provider would block the UI on
/// a network round trip and would still be unable to promise anything after a crash.
/// </para>
/// <para>
/// Only the methods backed by machinery that exists are implemented. The rest of §7's surface
/// arrives with the features that produce it.
/// </para>
/// </remarks>
/// <summary>The client-callable surface, declared so the TypeScript proxy is generated from it.</summary>
[Hub]
public interface IMailHub
{
	Task<IReadOnlyList<MailboxSummaryDto>> GetMailboxes(Guid accountId);

	Task<IReadOnlyList<MessageSummaryDto>> GetMessages(Guid mailboxId, int take);

	Task<IReadOnlyList<PendingChangeDto>> GetPendingSyncState(Guid accountId);

	Task SetFlags(Guid accountId, IReadOnlyList<Guid> messageIds, bool? isRead, bool? isFlagged);

	Task MoveMessages(Guid accountId, IReadOnlyList<Guid> messageIds, Guid targetMailboxId);

	Task MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds);
}

public class MailHub(MyloMailDbContext context, MutationQueue mutations) : Hub<IMailClient>, IMailHub
{
	public async Task<IReadOnlyList<MailboxSummaryDto>> GetMailboxes(Guid accountId)
	{
		var mailboxes = await context
			.Mailboxes.Where(m => m.AccountId == accountId)
			.Select(m => new
			{
				Mailbox = m,
				LocalCount = context.MessageMailboxes.Count(o => o.MailboxId == m.Id),
				Coverage = context
					.MailboxCoverageStates.Where(c => c.MailboxId == m.Id)
					.Select(c => (CoverageStatus?)c.Status)
					.FirstOrDefault(),
			})
			.ToListAsync();

		return
		[
			.. mailboxes
				.OrderBy(row => row.Mailbox.LocalSortOrder)
				.Select(row => new MailboxSummaryDto(
					row.Mailbox.Id,
					row.Mailbox.AccountId,
					row.Mailbox.ParentId,
					row.Mailbox.Name,
					row.Mailbox.SpecialUse,
					row.Mailbox.ProviderTotalCount,
					row.Mailbox.ProviderUnreadCount,
					row.LocalCount,
					row.Coverage ?? CoverageStatus.NotStarted
				)),
		];
	}

	public async Task<IReadOnlyList<MessageSummaryDto>> GetMessages(Guid mailboxId, int take)
	{
		var messages = await context
			.MessageMailboxes.Where(o => o.MailboxId == mailboxId)
			.Join(context.Messages, o => o.MessageId, m => m.Id, (_, m) => m)
			.ToListAsync();

		return
		[
			.. messages
				// Ordered here because SQLite cannot ORDER BY a DateTimeOffset.
				.OrderByDescending(m => m.ReceivedAt)
				.Take(take)
				.Select(m => new MessageSummaryDto(
					m.Id,
					m.AccountId,
					m.Subject,
					m.Snippet,
					m.From,
					m.ReceivedAt,
					m.IsRead,
					m.IsFlagged,
					m.HasNonInlineAttachments
				)),
		];
	}

	/// <summary>
	/// What the user has asked for that the server has not yet confirmed.
	/// </summary>
	/// <remarks>
	/// The renderer merges this over server-known state so a flag the user just toggled does
	/// not flicker back while its mutation is in flight (§6).
	/// </remarks>
	public async Task<IReadOnlyList<PendingChangeDto>> GetPendingSyncState(Guid accountId) =>
		await context
			.MessagePendingChanges.Join(
				context.Messages.Where(m => m.AccountId == accountId),
				p => p.MessageId,
				m => m.Id,
				(p, _) => new PendingChangeDto(p.MessageId, p.Field, p.DesiredValue)
			)
			.ToListAsync();

	public async Task SetFlags(Guid accountId, IReadOnlyList<Guid> messageIds, bool? isRead, bool? isFlagged)
	{
		foreach (var messageId in messageIds)
		{
			await mutations.SetFlagsAsync(accountId, messageId, new FlagUpdate(isRead, isFlagged));
		}
	}

	public async Task MoveMessages(Guid accountId, IReadOnlyList<Guid> messageIds, Guid targetMailboxId)
	{
		foreach (var messageId in messageIds)
		{
			await mutations.MoveAsync(accountId, messageId, targetMailboxId);
		}
	}

	public async Task MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds)
	{
		foreach (var messageId in messageIds)
		{
			await mutations.MoveToTrashAsync(accountId, messageId);
		}
	}
}

/// <summary>One locally desired change the server has not yet confirmed (§6).</summary>
[Tapper.TranspilationSource]
public record PendingChangeDto(Guid MessageId, MessageFlagField Field, bool DesiredValue);
