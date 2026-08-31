using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Content;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
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

	Task<MessageBodyDto> GetMessageBody(Guid messageId);

	Task<IReadOnlyList<AttachmentDto>> GetAttachmentMetadata(Guid messageId);

	Task<IReadOnlyList<MessageSummaryDto>> Search(Guid accountId, string query, Guid? mailboxId);

	Task<DraftDto> SaveDraft(SaveDraftRequest request);

	Task DeleteDraft(Guid draftId);

	Task<Guid> SendDraft(Guid draftId);

	Task<bool> CancelScheduledSend(Guid outboxItemId);

	Task CreateMailbox(Guid accountId, string name, Guid? parentId);

	Task RenameMailbox(Guid mailboxId, string newName);

	Task<bool> DeleteMailbox(Guid mailboxId);

	Task<AccountCapabilitiesDto> GetAccountCapabilities(Guid accountId);

	Task<AccountSettingsDto> UpdateAccount(AccountSettingsDto settings);

	Task SetFlags(Guid accountId, IReadOnlyList<Guid> messageIds, bool? isRead, bool? isFlagged);

	Task MoveMessages(Guid accountId, IReadOnlyList<Guid> messageIds, Guid targetMailboxId);

	Task MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds);
}

public class MailHub(
	MyloMailDbContext context,
	MutationQueue mutations,
	MessageSearch search,
	DraftService drafts,
	MailboxManagement mailboxes,
	IMailProviderFactory providers
) : Hub<IMailClient>, IMailHub
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

	/// <summary>
	/// A message's body, if it has been fetched.
	/// </summary>
	/// <remarks>
	/// Never fetches on demand: content acquisition is background work with its own ordering
	/// and its own failure handling, and a hub method that fetched would block the UI on a
	/// provider round trip while duplicating that logic.
	/// </remarks>
	public async Task<MessageBodyDto> GetMessageBody(Guid messageId)
	{
		var body = await context.MessageBodies.FirstOrDefaultAsync(b => b.MessageId == messageId);
		var state = await context.MessageContentStates.FirstOrDefaultAsync(c => c.MessageId == messageId);

		return new MessageBodyDto(
			messageId,
			body?.TextBody,
			body?.HtmlBody,
			state?.Status == ContentStatus.Indexed
		);
	}

	public async Task<IReadOnlyList<AttachmentDto>> GetAttachmentMetadata(Guid messageId) =>
		await context.Attachments
			.Where(a => a.MessageId == messageId)
			.OrderBy(a => a.Filename)
			.Select(a => new AttachmentDto(a.Id, a.MessageId, a.Filename, a.MimeType, a.Size, a.IsInline))
			.ToListAsync();

	/// <summary>
	/// Full-text search, optionally within one mailbox.
	/// </summary>
	/// <remarks>
	/// Scoping is applied after the match rather than indexed, so moving a message between
	/// folders never requires reindexing it (§8).
	/// </remarks>
	public Task<IReadOnlyList<MessageSummaryDto>> Search(Guid accountId, string query, Guid? mailboxId) =>
		search.SearchAsync(accountId, query, mailboxId);

	public async Task<DraftDto> SaveDraft(SaveDraftRequest request)
	{
		var draft = await drafts.SaveAsync(
			new DraftInput(
				request.DraftId,
				request.AccountId,
				request.InReplyToMessageId,
				request.To,
				request.Cc,
				request.Bcc,
				request.Subject,
				request.BodyHtml
			)
		);

		return new DraftDto(
			draft.Id,
			draft.AccountId,
			draft.To,
			draft.Cc,
			draft.Bcc,
			draft.Subject,
			draft.BodyHtml,
			[.. draft.Attachments.Select(a => new DraftAttachmentDto(a.Id, a.Filename, a.MimeType, a.Size, a.IsInline))]
		);
	}

	public Task DeleteDraft(Guid draftId) => drafts.DeleteAsync(draftId);

	/// <summary>
	/// Queues a draft for sending and returns the outbox item, which is what cancellation
	/// addresses during the undo window (§15).
	/// </summary>
	public async Task<Guid> SendDraft(Guid draftId) => (await drafts.SendAsync(draftId)).Id;

	/// <summary>
	/// Attempts to cancel. False means the worker already took it, and the answer is final:
	/// once sending, the message may be on its way and pretending otherwise would be a lie.
	/// </summary>
	public Task<bool> CancelScheduledSend(Guid outboxItemId) => drafts.CancelSendAsync(outboxItemId);

	public Task CreateMailbox(Guid accountId, string name, Guid? parentId) =>
		mailboxes.CreateAsync(accountId, name, parentId);

	public Task RenameMailbox(Guid mailboxId, string newName) =>
		mailboxes.RenameAsync(mailboxId, newName);

	/// <summary>Returns whether the messages went with the folder, which differs by provider (§2).</summary>
	public Task<bool> DeleteMailbox(Guid mailboxId) => mailboxes.DeleteAsync(mailboxId);

	/// <summary>
	/// What the provider does, for the questions the UI has to ask before acting.
	/// </summary>
	/// <remarks>
	/// Deleting an IMAP or Graph folder destroys the mail in it; deleting a Gmail label
	/// leaves the messages in All Mail. A confirmation worded for one is wrong for the other,
	/// so the UI asks rather than assuming (§2).
	/// </remarks>
	public async Task<AccountCapabilitiesDto> GetAccountCapabilities(Guid accountId)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == accountId);
		return new AccountCapabilitiesDto(
			accountId,
			providers.For(account).Capabilities.DeletingMailboxDeletesMessages
		);
	}

	/// <summary>
	/// Updates the settings a user can change.
	/// </summary>
	/// <remarks>
	/// Deliberately not everything on <c>Account</c>: auth state and sync progress are the
	/// system's to write, and letting a settings screen set them would make the UI a second
	/// source of truth for facts it does not observe (§1).
	/// </remarks>
	public async Task<AccountSettingsDto> UpdateAccount(AccountSettingsDto settings)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == settings.Id);

		account.DisplayName = settings.DisplayName;
		account.Color = settings.Color;
		account.PollIntervalSeconds = Math.Max(settings.PollIntervalSeconds, 15);
		account.PollingEnabled = settings.PollingEnabled;
		account.UndoSendDelaySeconds = Math.Max(settings.UndoSendDelaySeconds, 0);
		account.NotificationsEnabled = settings.NotificationsEnabled;

		await context.SaveChangesAsync();
		return settings with
		{
			PollIntervalSeconds = account.PollIntervalSeconds,
			UndoSendDelaySeconds = account.UndoSendDelaySeconds,
		};
	}

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
