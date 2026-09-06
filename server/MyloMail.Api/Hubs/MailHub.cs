using Hangfire;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Content;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Security;
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

	Task<IReadOnlyList<MessageSummaryDto>> GetMessages(Guid mailboxId, int skip, int take);

	Task<IReadOnlyList<PendingChangeDto>> GetPendingSyncState(Guid accountId);

	Task<MessageBodyDto> GetMessageBody(Guid messageId);

	/// <summary>
	/// The meeting invite this message carries, if any (§13 Epic 7) — null for an ordinary
	/// message, and also null until the message's raw content has been fetched (this reads the
	/// same stored bytes <see cref="GetMessageBody"/> derives its content from, not a live
	/// re-fetch).
	/// </summary>
	Task<MessageInviteDto?> GetMessageInvite(Guid messageId);

	Task<IReadOnlyList<AttachmentDto>> GetAttachmentMetadata(Guid messageId);

	/// <summary>
	/// The account's attachment limits (§15), for Compose to check before send. Reported
	/// honestly rather than as a single number — see <see cref="AttachmentConstraintsDto"/>.
	/// </summary>
	Task<AttachmentConstraintsDto> GetAttachmentConstraints(Guid accountId);

	/// <summary>
	/// The connectivity-aware pause logic's current state (§7, §15), so a window opened while
	/// already offline shows the calm banner immediately rather than waiting for the next
	/// transition — <c>ConnectivityChanged</c> only fires on a change, not on connect.
	/// </summary>
	Task<bool> GetConnectivity();

	/// <summary>The address/subject/date fields a reply or forward is built from (§13).</summary>
	Task<MessageReplyContextDto> GetMessageReplyContext(Guid messageId);

	Task<IReadOnlyList<MessageSummaryDto>> Search(Guid accountId, string query, Guid? mailboxId);

	/// <summary>Structured drafts, including drafts discovered from the server's Drafts mailbox.</summary>
	Task<IReadOnlyList<DraftDto>> GetDrafts(Guid accountId);

	/// <summary>
	/// This account's send-as identities, default first, for compose's identity picker (§1,
	/// §15). Always at least one row — every account has a default identity.
	/// </summary>
	Task<IReadOnlyList<SendIdentityDto>> GetSendIdentities(Guid accountId);

	/// <summary>Adds a send-as identity — the account's first ever becomes its default (§1, §15).</summary>
	Task<SendIdentityDto> AddSendIdentity(
		Guid accountId,
		string displayName,
		string emailAddress,
		string? signatureHtml
	);

	Task<SendIdentityDto> UpdateSendIdentity(
		Guid identityId,
		string displayName,
		string emailAddress,
		string? signatureHtml
	);

	/// <summary>Promotes one identity to the account's default, demoting whichever one held it (§1).</summary>
	Task<SendIdentityDto> SetDefaultSendIdentity(Guid identityId);

	/// <exception cref="HubException">
	/// The identity is the account's default, or a saved draft still points at it.
	/// </exception>
	Task DeleteSendIdentity(Guid identityId);

	Task<DraftDto> SaveDraft(SaveDraftRequest request);

	/// <summary>
	/// Resolves a draft flagged <c>SyncConflict</c> (§1, §15): the server's copy changed
	/// while this one was being edited locally. <paramref name="keepMine"/> true keeps the
	/// local version (abandoning the conflicting remote draft and pushing a fresh one); false
	/// discards the local edit and pulls the server's actual current content instead.
	/// </summary>
	Task<DraftDto> ResolveDraftConflict(Guid draftId, bool keepMine);

	Task DeleteDraft(Guid draftId);

	/// <summary>
	/// Queues a draft for sending. <paramref name="scheduledFor"/> is null for a normal
	/// send (the account's undo-send delay applies) or an explicit future time for a
	/// scheduled send (§15) — both go through the same outbox mechanism.
	/// </summary>
	/// <exception cref="HubException">
	/// The draft has an unresolved sync conflict, or an attachment exceeds a known size
	/// constraint for this account (§15).
	/// </exception>
	Task<Guid> SendDraft(Guid draftId, DateTimeOffset? scheduledFor = null);

	Task<bool> CancelScheduledSend(Guid outboxItemId);

	Task CreateMailbox(Guid accountId, string name, Guid? parentId);

	Task RenameMailbox(Guid mailboxId, string newName);

	Task<bool> DeleteMailbox(Guid mailboxId);

	/// <summary>Drag-a-folder-onto-a-folder reparenting (§13 Epic 2). Null moves it to the root.</summary>
	Task MoveMailbox(Guid mailboxId, Guid? newParentId);

	/// <summary>
	/// Sidebar drag-reorder among siblings of one parent (§13 Epic 2) — purely local, since no
	/// provider supports arbitrary folder ordering.
	/// </summary>
	Task ReorderMailboxes(Guid accountId, Guid? parentId, IReadOnlyList<Guid> orderedMailboxIds);

	/// <summary>Sidebar expand/collapse for one folder, persisted across restarts (§13 Epic 2)
	/// — purely local, the same as <see cref="ReorderMailboxes"/>.</summary>
	Task SetMailboxCollapsed(Guid mailboxId, bool collapsed);

	/// <summary>
	/// Bounds this one mailbox's initial sync differently from the account's own choice —
	/// e.g. a large "All Mail" label discovered after account setup (§3, §13 Epic 3). Null
	/// mode clears the override, reverting to the account's default. Only meaningful before
	/// this mailbox's own backfill has started; the caller decides whether to offer it.
	/// </summary>
	/// <exception cref="HubException">A non-null, non-Full mode with no positive bound value.</exception>
	Task SetMailboxInitialSyncOverride(Guid mailboxId, InitialSyncMode? mode, int? boundValue);

	/// <summary>
	/// Corrects a mailbox's special-use role (Sent/Trash/Drafts/Archive/Junk) by hand — for a
	/// server that doesn't advertise RFC 6154 SPECIAL-USE, whose folder names the provider's
	/// own name-based fallback guessed wrong or didn't recognise (§13 Epic 2). Highest
	/// precedence over both a real server attribute and the fallback guess; null clears it,
	/// reverting to whatever the provider itself reports.
	/// </summary>
	Task SetMailboxSpecialUseOverride(Guid mailboxId, SpecialUse? specialUse);

	Task<AccountCapabilitiesDto> GetAccountCapabilities(Guid accountId);

	Task<AccountSettingsDto> UpdateAccount(AccountSettingsDto settings);

	/// <summary>
	/// Sidebar drag-reorder among accounts (§13 Epic 1) — purely local, the same as
	/// <see cref="ReorderMailboxes"/>: no provider has a concept of account ordering.
	/// </summary>
	Task ReorderAccounts(IReadOnlyList<Guid> orderedAccountIds);

	/// <summary>Sidebar section expand/collapse for one account, persisted across restarts
	/// (§13 Epic 2) — purely local, the same as <see cref="ReorderAccounts"/>.</summary>
	Task SetAccountSidebarCollapsed(Guid accountId, bool collapsed);

	/// <summary>
	/// Pins a certificate for this account and hostname (§15) — offered only after normal TLS
	/// validation has already failed and the user has seen the fingerprint/issuer this rejected
	/// certificate presents; never called speculatively.
	/// </summary>
	Task TrustCertificate(Guid accountId, string hostname, string sha256Fingerprint);

	Task SetFlags(Guid accountId, IReadOnlyList<Guid> messageIds, bool? isRead, bool? isFlagged);

	Task MoveMessages(Guid accountId, IReadOnlyList<Guid> messageIds, Guid targetMailboxId);

	/// <summary>
	/// Drops one membership without deleting the message (§6) — meaningful only where a
	/// message can belong to several mailboxes at once, e.g. un-labelling in Gmail. Distinct
	/// from <see cref="MoveToTrash"/>: this never touches the message itself.
	/// </summary>
	/// <remarks>
	/// No renderer caller yet: IMAP's provider implementation is a placeholder that expunges
	/// the message outright (its own comment calls this "an implementation shortcut, not a
	/// statement they are the same operation"), and Graph's forwards straight to permanent
	/// deletion. Exposing this in the message-list menu today would make "remove from this
	/// folder" silently irreversible on both. Wire it into the UI once that per-provider
	/// divergence lands — Gmail's implementation is already correct.
	/// </remarks>
	Task RemoveFromMailbox(Guid accountId, IReadOnlyList<Guid> messageIds, Guid mailboxId);

	Task MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds);

	/// <summary>Deletes the message outright — never reversible by the app (§6).</summary>
	Task DeletePermanently(Guid accountId, IReadOnlyList<Guid> messageIds);

	Task<IReadOnlyList<CalendarSummaryDto>> GetCalendars(Guid accountId);

	Task<IReadOnlyList<CalendarEventSummaryDto>> GetCalendarEvents(Guid calendarId, DateTimeOffset from, DateTimeOffset to);

	Task<CalendarEventSummaryDto> SaveCalendarEvent(SaveCalendarEventRequest request);

	Task DeleteCalendarEvent(Guid eventId);

	/// <summary>Organiser, attendees and this account's own response, for the event detail
	/// view (§13 Epic 7) — not carried on <see cref="GetCalendarEvents"/>'s summary DTO.</summary>
	Task<CalendarEventDetailDto> GetCalendarEventDetail(Guid eventId);

	/// <summary>Accept/Decline/Tentative on an invite (§13 Epic 7).</summary>
	Task RespondToInvite(Guid eventId, InviteResponse response, string? comment);

	/// <summary>
	/// "Keep mine" (<paramref name="keepMine"/> true, force-overwrite the server) or "keep
	/// theirs" (false, discard the local edit and pull the server's current version) for a
	/// flagged calendar conflict (§15). A no-op if the event is not currently flagged.
	/// </summary>
	Task<CalendarEventSummaryDto> ResolveEventConflict(Guid eventId, bool keepMine);

	/// <summary>Confirms the shell showed a notification at least once (§13 Epic 9).</summary>
	Task MarkNotificationDelivered(Guid notificationId);

	/// <summary>
	/// Fetches the message a still-staged notification points at, on demand, rather than the
	/// navigation simply failing (§3). Null if the account's staged history still doesn't
	/// resolve it — the caller only knows this notification was recorded, not why it might be
	/// slow.
	/// </summary>
	Task<Guid?> ResolveStagedMessage(Guid notificationId);

	/// <summary>The raw MIME bytes for one message, base64-encoded, fetched on demand if needed.</summary>
	Task<string> SaveMessageAsEml(Guid messageId);

	Task<Guid> StartBulkExport(Guid accountId, string destinationPath);

	Task CancelBulkExport(Guid exportId);

	/// <summary>The account's most recent export, if it has ever run one (§13 Export).</summary>
	Task<ExportJobDto?> GetExportStatus(Guid accountId);
}

public class MailHub(
	MyloMailDbContext context,
	MutationQueue mutations,
	MessageSearch search,
	DraftService drafts,
	SendIdentityService identities,
	MailboxManagement mailboxes,
	CalendarEventService calendarEvents,
	Notifications.NotificationService notifications,
	ChangeStreamService changeStream,
	IMailProviderFactory providers,
	Scheduling.ExportJobs export,
	ITrustedCertificateStore certificates,
	IBackgroundJobClient jobs,
	Scheduling.ConnectivityMonitor connectivity,
	Scheduling.PollRegistry polls
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
					row.Coverage ?? CoverageStatus.NotStarted,
					row.Mailbox.IsCollapsed,
					row.Mailbox.InitialSyncModeOverride,
					row.Mailbox.InitialSyncBoundValueOverride,
					row.Mailbox.ProviderMailboxId is null,
					row.Mailbox.SpecialUseOverride
				)),
		];
	}

	public async Task<IReadOnlyList<MessageSummaryDto>> GetMessages(Guid mailboxId, int skip, int take)
	{
		var messages = await context
			.MessageMailboxes.Where(o => o.MailboxId == mailboxId)
			.Join(context.Messages, o => o.MessageId, m => m.Id, (_, m) => m)
			.ToListAsync();

		// Id as a tiebreaker, not just ReceivedAt: two messages can share a timestamp (a
		// provider-side bulk import, or a fast IMAP APPEND burst), and a total order matters
		// here specifically because paging calls this same query again for the next page — an
		// order that reshuffles ties between calls would skip or repeat a message at the page
		// boundary even though nothing in the mailbox actually changed.
		var shown = messages
			.OrderByDescending(m => m.ReceivedAt)
			.ThenByDescending(m => m.Id)
			.Skip(skip)
			.Take(take)
			.ToList();
		var failures = await MessageMutationFailures.ForMessagesAsync(
			context,
			shown.Select(m => m.Id).ToList()
		);

		return
		[
			.. shown.Select(m => new MessageSummaryDto(
				m.Id,
				m.AccountId,
				m.Subject,
				m.Snippet,
				m.From,
				m.ReceivedAt,
				m.IsRead,
				m.IsFlagged,
				m.HasNonInlineAttachments,
				failures.TryGetValue(m.Id, out var category) ? category : null
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
		// Distinct from "not yet fetched," which has no MessageBody/MessageContentState row
		// either but a live Message row behind it: a reading pane left open on a message that
		// has since been deleted (or its whole account removed) would otherwise see the same
		// all-null shape and poll this forever, showing a blank body under a stale subject
		// line with no indication anything is wrong.
		if (!await context.Messages.AnyAsync(m => m.Id == messageId))
		{
			throw new HubException("This message no longer exists.");
		}

		var body = await context.MessageBodies.FirstOrDefaultAsync(b => b.MessageId == messageId);
		var state = await context.MessageContentStates.FirstOrDefaultAsync(c => c.MessageId == messageId);

		return new MessageBodyDto(
			messageId,
			body?.TextBody,
			body?.HtmlBody,
			state?.Status == ContentStatus.Indexed,
			state?.Status == ContentStatus.Failed
		);
	}

	public async Task<MessageInviteDto?> GetMessageInvite(Guid messageId)
	{
		var raw = await context.MessageRaws.FirstOrDefaultAsync(r => r.MessageId == messageId);
		if (raw is null)
		{
			return null;
		}

		using var stream = new MemoryStream(raw.Content);
		var mime = await MimeKit.MimeMessage.LoadAsync(stream);
		var part = mime.BodyParts
			.OfType<MimeKit.MimePart>()
			.FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"));
		if (part?.Content is null)
		{
			return null;
		}

		using var decoded = new MemoryStream();
		await part.Content.DecodeToAsync(decoded);
		var ics = System.Text.Encoding.UTF8.GetString(decoded.ToArray());
		if (!string.Equals(CalDavIcs.ParseMethod(ics), "REQUEST", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var parsed = CalDavIcs
			.ParseEvents(ics, $"mail:{messageId}", string.Empty)
			.FirstOrDefault(e => e.RecurrenceId is null);
		if (parsed is null)
		{
			return null;
		}

		var message = await context.Messages.FirstAsync(m => m.Id == messageId);
		var calendarIds = await context
			.Calendars.Where(c => c.AccountId == message.AccountId)
			.Select(c => c.Id)
			.ToListAsync();
		var ev = await context.CalendarEvents.FirstOrDefaultAsync(e =>
			e.ICalUid == parsed.ICalUid && calendarIds.Contains(e.CalendarId)
		);

		InviteResponse? myResponse = null;
		if (ev is not null)
		{
			var myEmail = await context
				.SendIdentities.Where(i => i.AccountId == message.AccountId && i.IsDefault)
				.Select(i => i.EmailAddress)
				.FirstAsync();
			var mine = ev.Attendees.FirstOrDefault(a =>
				string.Equals(a.Email, myEmail, StringComparison.OrdinalIgnoreCase)
			);
			myResponse = mine is null ? null : ToInviteResponse(mine.ResponseStatus);
		}

		return new MessageInviteDto(
			ev?.Id,
			parsed.Title,
			parsed.Start,
			parsed.End,
			parsed.IsAllDay,
			parsed.Organizer,
			myResponse
		);
	}

	public async Task<IReadOnlyList<AttachmentDto>> GetAttachmentMetadata(Guid messageId) =>
		await context.Attachments
			.Where(a => a.MessageId == messageId)
			.OrderBy(a => a.Filename)
			.Select(a => new AttachmentDto(a.Id, a.MessageId, a.Filename, a.MimeType, a.Size, a.IsInline))
			.ToListAsync();

	public async Task<MessageReplyContextDto> GetMessageReplyContext(Guid messageId)
	{
		var message = await context.Messages.FirstAsync(m => m.Id == messageId);
		return new MessageReplyContextDto(
			message.Id,
			message.From,
			message.To,
			message.Cc,
			message.ReplyToAddresses,
			message.Subject,
			message.ReceivedAt
		);
	}

	/// <summary>
	/// Full-text search, optionally within one mailbox.
	/// </summary>
	/// <remarks>
	/// Scoping is applied after the match rather than indexed, so moving a message between
	/// folders never requires reindexing it (§8).
	/// </remarks>
	public Task<IReadOnlyList<MessageSummaryDto>> Search(Guid accountId, string query, Guid? mailboxId) =>
		search.SearchAsync(accountId, query, mailboxId);

	public async Task<IReadOnlyList<DraftDto>> GetDrafts(Guid accountId) =>
		[
			.. (await context.Drafts.Where(d => d.AccountId == accountId).OrderByDescending(d => d.SavedAt).ToListAsync())
				.Select(ToDto),
		];

	public async Task<IReadOnlyList<SendIdentityDto>> GetSendIdentities(Guid accountId) =>
		[
			.. (await context
				.SendIdentities.Where(i => i.AccountId == accountId)
				.OrderByDescending(i => i.IsDefault)
				.ToListAsync())
				.Select(i => new SendIdentityDto(
					i.Id,
					i.AccountId,
					i.DisplayName,
					i.EmailAddress,
					i.SignatureHtml,
					i.IsDefault
				)),
		];

	public async Task<SendIdentityDto> AddSendIdentity(
		Guid accountId,
		string displayName,
		string emailAddress,
		string? signatureHtml
	)
	{
		try
		{
			return ToSendIdentityDto(
				await identities.AddAsync(accountId, displayName, emailAddress, signatureHtml)
			);
		}
		catch (InvalidOperationException ex)
		{
			throw new HubException(ex.Message);
		}
	}

	public async Task<SendIdentityDto> UpdateSendIdentity(
		Guid identityId,
		string displayName,
		string emailAddress,
		string? signatureHtml
	)
	{
		try
		{
			return ToSendIdentityDto(
				await identities.UpdateAsync(identityId, displayName, emailAddress, signatureHtml)
			);
		}
		catch (InvalidOperationException ex)
		{
			throw new HubException(ex.Message);
		}
	}

	public async Task<SendIdentityDto> SetDefaultSendIdentity(Guid identityId) =>
		ToSendIdentityDto(await identities.SetDefaultAsync(identityId));

	public async Task DeleteSendIdentity(Guid identityId)
	{
		try
		{
			await identities.DeleteAsync(identityId);
		}
		catch (InvalidOperationException ex)
		{
			throw new HubException(ex.Message);
		}
	}

	private static SendIdentityDto ToSendIdentityDto(SendIdentity identity) =>
		new(
			identity.Id,
			identity.AccountId,
			identity.DisplayName,
			identity.EmailAddress,
			identity.SignatureHtml,
			identity.IsDefault
		);

	public async Task<DraftDto> SaveDraft(SaveDraftRequest request)
	{
		var draft = await drafts.SaveAsync(
			new DraftInput(
				request.DraftId,
				request.AccountId,
				request.SendIdentityId,
				request.InReplyToMessageId,
				request.To,
				request.Cc,
				request.Bcc,
				request.Subject,
				request.BodyHtml
			)
		);

		return ToDto(draft);
	}

	private static DraftDto ToDto(Draft draft) =>
		new(
			draft.Id,
			draft.AccountId,
			draft.SendIdentityId,
			draft.InReplyToMessageId,
			draft.To,
			draft.Cc,
			draft.Bcc,
			draft.Subject,
			draft.BodyHtml,
			[.. draft.Attachments.Select(a => new DraftAttachmentDto(a.Id, a.Filename, a.MimeType, a.Size, a.IsInline))],
			draft.SyncConflict
		);

	public async Task<DraftDto> ResolveDraftConflict(Guid draftId, bool keepMine) =>
		ToDto(await drafts.ResolveConflictAsync(draftId, keepMine));

	public Task DeleteDraft(Guid draftId) => drafts.DeleteAsync(draftId);

	/// <summary>
	/// Queues a draft for sending and returns the outbox item, which is what cancellation
	/// addresses during the undo window (§15).
	/// </summary>
	public async Task<Guid> SendDraft(Guid draftId, DateTimeOffset? scheduledFor = null)
	{
		try
		{
			return (await drafts.SendAsync(draftId, scheduledFor)).Id;
		}
		catch (InvalidOperationException ex)
		{
			// Without this, SignalR (no detailed-errors) genericises the message to something
			// unhelpful — losing exactly the "which attachment" / "unresolved conflict" detail
			// DraftService.SendAsync's exception carries, the same reason DeleteSendIdentity
			// above does this.
			throw new HubException(ex.Message);
		}
	}

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

	public Task MoveMailbox(Guid mailboxId, Guid? newParentId) => mailboxes.MoveAsync(mailboxId, newParentId);

	public Task ReorderMailboxes(Guid accountId, Guid? parentId, IReadOnlyList<Guid> orderedMailboxIds) =>
		mailboxes.ReorderAsync(accountId, parentId, orderedMailboxIds);

	public async Task SetMailboxCollapsed(Guid mailboxId, bool collapsed)
	{
		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == mailboxId);
		mailbox.IsCollapsed = collapsed;
		await context.SaveChangesAsync();
	}

	public async Task SetMailboxInitialSyncOverride(Guid mailboxId, InitialSyncMode? mode, int? boundValue)
	{
		if (mode is not null and not InitialSyncMode.Full && boundValue is not > 0)
		{
			throw new HubException("A bounded initial sync needs a positive month/message count.");
		}

		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == mailboxId);
		mailbox.InitialSyncModeOverride = mode;
		mailbox.InitialSyncBoundValueOverride = mode == InitialSyncMode.Full ? null : boundValue;
		await context.SaveChangesAsync();
	}

	public async Task SetMailboxSpecialUseOverride(Guid mailboxId, SpecialUse? specialUse)
	{
		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == mailboxId);
		mailbox.SpecialUseOverride = specialUse;
		await context.SaveChangesAsync();
	}

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

	/// <inheritdoc cref="IMailHub.GetAttachmentConstraints" />
	public async Task<AttachmentConstraintsDto> GetAttachmentConstraints(Guid accountId)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == accountId);
		var constraints = await providers.For(account).GetAttachmentConstraintsAsync(account, default);
		return new AttachmentConstraintsDto(
			constraints.ApiPerFileLimit,
			constraints.KnownMessageSizeLimit,
			constraints.ConfiguredOverride,
			constraints.IsUnknown
		);
	}

	/// <inheritdoc cref="IMailHub.GetConnectivity" />
	public Task<bool> GetConnectivity() => Task.FromResult(connectivity.IsOnline);

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
		var resumingPolling = settings.PollingEnabled && !account.PollingEnabled;

		account.DisplayName = settings.DisplayName;
		account.Color = settings.Color;
		account.PollIntervalSeconds = Math.Max(settings.PollIntervalSeconds, 15);
		account.PollingEnabled = settings.PollingEnabled;
		account.UndoSendDelaySeconds = Math.Max(settings.UndoSendDelaySeconds, 0);
		account.NotificationsEnabled = settings.NotificationsEnabled;
		account.CertificateTrustMode = settings.CertificateTrustMode;
		account.AttachmentSizeLimitOverride =
			settings.AttachmentSizeLimitOverride is > 0 ? settings.AttachmentSizeLimitOverride : null;

		// IMAP only: a non-null value from any other account type is ignored rather than
		// throwing, since the renderer only ever shows this field for IMAP accounts and a
		// stray value here would otherwise reject an unrelated field's update too.
		if (settings.AppendToSentOnSend is bool appendToSentOnSend && account.ProviderConfig is ImapProviderConfig imap)
		{
			imap.AppendToSentOnSend = appendToSentOnSend;
		}

		await context.SaveChangesAsync();

		if (resumingPolling)
		{
			// Deliberately no polls.StopAll(account.Id) here (unlike
			// StartupScheduler.ResumeAccountAsync, which is safe to clear unconditionally
			// because a job that hit ProviderAuthenticationException already stopped and
			// released its own slot before that path runs). A loop disabled and re-enabled
			// fast enough may not have ticked yet, so it may still hold its registry slot
			// without having stopped — force-clearing it here would let TopologyAsync's
			// TryStart claim a second, concurrent loop for the same scope, doubling the poll
			// rate. Leaving the slot alone means: if the old loop already noticed and
			// stopped, TryStart below claims it correctly; if it hasn't yet, TryStart simply
			// no-ops and the still-alive loop resumes itself on its own next tick, since
			// PollingEnabled is true again by then.
			if (polls.TryStart(account.Id, Scheduling.SyncJobs.TopologyScope))
			{
				jobs.Enqueue<Scheduling.SyncJobs>(j => j.TopologyAsync(account.Id, default));
			}
		}
		return settings with
		{
			PollIntervalSeconds = account.PollIntervalSeconds,
			UndoSendDelaySeconds = account.UndoSendDelaySeconds,
			AppendToSentOnSend = (account.ProviderConfig as ImapProviderConfig)?.AppendToSentOnSend,
		};
	}

	public async Task ReorderAccounts(IReadOnlyList<Guid> orderedAccountIds)
	{
		var accounts = await context.Accounts.ToDictionaryAsync(a => a.Id);
		for (var index = 0; index < orderedAccountIds.Count; index++)
		{
			if (accounts.TryGetValue(orderedAccountIds[index], out var account))
			{
				account.SortOrder = index;
			}
		}

		await context.SaveChangesAsync();
	}

	public async Task SetAccountSidebarCollapsed(Guid accountId, bool collapsed)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == accountId);
		account.SidebarCollapsed = collapsed;
		await context.SaveChangesAsync();
	}

	public Task TrustCertificate(Guid accountId, string hostname, string sha256Fingerprint) =>
		certificates.TrustAsync(accountId, hostname, sha256Fingerprint, default);

	public Task SetFlags(Guid accountId, IReadOnlyList<Guid> messageIds, bool? isRead, bool? isFlagged) =>
		EnqueueEachAsync(
			messageIds,
			messageId => mutations.SetFlagsAsync(accountId, messageId, new FlagUpdate(isRead, isFlagged))
		);

	public Task MoveMessages(Guid accountId, IReadOnlyList<Guid> messageIds, Guid targetMailboxId) =>
		EnqueueEachAsync(messageIds, messageId => mutations.MoveAsync(accountId, messageId, targetMailboxId));

	public Task RemoveFromMailbox(Guid accountId, IReadOnlyList<Guid> messageIds, Guid mailboxId) =>
		EnqueueEachAsync(
			messageIds,
			messageId => mutations.RemoveFromMailboxAsync(accountId, messageId, mailboxId)
		);

	public Task MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds) =>
		EnqueueEachAsync(messageIds, messageId => mutations.MoveToTrashAsync(accountId, messageId));

	public Task DeletePermanently(Guid accountId, IReadOnlyList<Guid> messageIds) =>
		EnqueueEachAsync(messageIds, messageId => mutations.DeletePermanentlyAsync(accountId, messageId));

	/// <summary>
	/// Attempts every id in a bulk action rather than stopping at the first failure — one
	/// message rejected (a stale selection, a concurrent enqueue collision on the unique
	/// <c>(AccountId, MessageId, Sequence)</c> index) must not silently strand every message
	/// after it in the same multi-select untouched. Failures are collected and reported
	/// together once every id has been tried, rather than either swallowed or aborting early.
	/// </summary>
	private static async Task EnqueueEachAsync(IReadOnlyList<Guid> messageIds, Func<Guid, Task> enqueue)
	{
		List<Exception>? failures = null;
		foreach (var messageId in messageIds)
		{
			try
			{
				await enqueue(messageId);
			}
			catch (Exception ex)
			{
				(failures ??= []).Add(ex);
			}
		}
		if (failures is { Count: > 0 })
		{
			// A HubException specifically: SignalR replaces any other exception type's message
			// with a generic fallback on the wire (no EnableDetailedErrors here), so an
			// AggregateException's carefully built summary would never actually reach the
			// renderer's error toast — every other hub method in this file that surfaces a
			// message to the caller does the same for the same reason.
			throw new HubException($"{failures.Count} of {messageIds.Count} message(s) could not be updated.");
		}
	}

	public async Task<IReadOnlyList<CalendarSummaryDto>> GetCalendars(Guid accountId) =>
		await context
			.Calendars.Where(c => c.AccountId == accountId)
			.Select(c => new CalendarSummaryDto(c.Id, c.AccountId, c.Name, c.Colour, c.IsDefault))
			.ToListAsync();

	/// <summary>
	/// Events overlapping <paramref name="from"/>/<paramref name="to"/>, not merely starting
	/// within it — including a recurring master's own occurrences within the window, expanded
	/// query-time (§13 Epic 7).
	/// </summary>
	public Task<IReadOnlyList<CalendarEventSummaryDto>> GetCalendarEvents(
		Guid calendarId,
		DateTimeOffset from,
		DateTimeOffset to
	) => CalendarEventOccurrences.ForCalendarAsync(context, calendarId, from, to);

	public async Task<CalendarEventSummaryDto> SaveCalendarEvent(SaveCalendarEventRequest request)
	{
		var saved = await calendarEvents.SaveAsync(
			new CalendarEventInput(
				request.EventId,
				request.CalendarId,
				request.Title,
				request.Location,
				request.Description,
				request.Start,
				request.End,
				request.IsAllDay
			)
		);
		return ToSummaryDto(saved);
	}

	public Task DeleteCalendarEvent(Guid eventId) => calendarEvents.DeleteAsync(eventId);

	public async Task<CalendarEventSummaryDto> ResolveEventConflict(Guid eventId, bool keepMine) =>
		ToSummaryDto(await calendarEvents.ResolveConflictAsync(eventId, keepMine));

	private static CalendarEventSummaryDto ToSummaryDto(CalendarEvent ev) =>
		new(
			ev.Id,
			ev.CalendarId,
			ev.Title,
			ev.Location,
			ev.Description,
			ev.Start,
			ev.End,
			ev.IsAllDay,
			ev.Status,
			ev.RecurrenceRules.Count > 0 || ev.RecurrenceMasterId != null,
			ev.SyncConflict,
			false,
			null,
			ev.RecurrenceRules.Count > 0
		);

	public async Task<CalendarEventDetailDto> GetCalendarEventDetail(Guid eventId)
	{
		var ev = await context.CalendarEvents.FirstAsync(e => e.Id == eventId);
		var calendar = await context.Calendars.FirstAsync(c => c.Id == ev.CalendarId);
		var myEmail = await context
			.SendIdentities.Where(i => i.AccountId == calendar.AccountId && i.IsDefault)
			.Select(i => i.EmailAddress)
			.FirstAsync();

		var isOrganizer = string.Equals(ev.Organizer?.Email, myEmail, StringComparison.OrdinalIgnoreCase);
		var mine = ev.Attendees.FirstOrDefault(a =>
			string.Equals(a.Email, myEmail, StringComparison.OrdinalIgnoreCase)
		);

		return new CalendarEventDetailDto(
			ev.Id,
			ev.Organizer,
			[.. ev.Attendees.Select(a => new AttendeeDto(a.Name, a.Email, a.Role, a.ResponseStatus))],
			isOrganizer,
			mine is null ? null : ToInviteResponse(mine.ResponseStatus),
			ev.Reminders,
			ev.Start,
			ev.End,
			ev.IsAllDay
		);
	}

	public Task RespondToInvite(Guid eventId, InviteResponse response, string? comment) =>
		calendarEvents.RespondToInviteAsync(eventId, response, comment);

	private static InviteResponse? ToInviteResponse(Domain.ResponseStatus status) =>
		status switch
		{
			Domain.ResponseStatus.Accepted => InviteResponse.Accept,
			Domain.ResponseStatus.Declined => InviteResponse.Decline,
			Domain.ResponseStatus.Tentative => InviteResponse.Tentative,
			_ => null,
		};

	public Task MarkNotificationDelivered(Guid notificationId) =>
		notifications.MarkDeliveredAsync(notificationId);

	public async Task<Guid?> ResolveStagedMessage(Guid notificationId)
	{
		var record = await context.NotificationRecords.FirstOrDefaultAsync(n => n.Id == notificationId);
		if (record is null)
		{
			return null;
		}
		if (record.MessageId is Guid resolved)
		{
			return resolved;
		}

		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == record.AccountId);
		if (account is null)
		{
			return null;
		}

		// Draining the whole staged queue, not just this one notification's event: replay
		// only ever proceeds in order (§3), and the record this click is asking about is
		// already known to have been staged, so its event is somewhere in that queue.
		await changeStream.ReplayStagedAsync(account);

		return await context.NotificationRecords.AsNoTracking()
			.Where(n => n.Id == notificationId)
			.Select(n => n.MessageId)
			.FirstOrDefaultAsync();
	}

	public async Task<string> SaveMessageAsEml(Guid messageId)
	{
		var occurrence = await context.MessageMailboxes.FirstAsync(o => o.MessageId == messageId);
		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == occurrence.MailboxId);
		var account = await context.Accounts.FirstAsync(a => a.Id == mailbox.AccountId);
		var raw = await export.RawBytesAsync(account, messageId);
		return Convert.ToBase64String(raw);
	}

	public Task<Guid> StartBulkExport(Guid accountId, string destinationPath) =>
		export.StartAsync(accountId, destinationPath);

	public Task CancelBulkExport(Guid exportId) => export.RequestCancelAsync(exportId);

	public async Task<ExportJobDto?> GetExportStatus(Guid accountId)
	{
		var job = await export.GetLatestAsync(accountId);
		return job is null
			? null
			: new ExportJobDto(job.Id, job.Status, job.WrittenCount, job.TotalCount, job.LastError);
	}
}

/// <summary>One locally desired change the server has not yet confirmed (§6).</summary>
[Tapper.TranspilationSource]
public record PendingChangeDto(Guid MessageId, MessageFlagField Field, bool DesiredValue);
