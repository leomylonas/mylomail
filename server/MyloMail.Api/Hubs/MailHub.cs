using Hangfire;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Contacts;
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

	Task<IReadOnlyList<MessageSummaryDto>> GetThreadMessages(Guid mailboxId, string threadId);

	Task SetActiveMailbox(Guid accountId, Guid mailboxId);

	Task<IReadOnlyList<PendingChangeDto>> GetPendingSyncState(Guid accountId);

	/// <summary>
	/// Which exact optimistic membership claims became terminal while a renderer was
	/// disconnected. SignalR does not replay missed events, so reconnect reconciliation reads
	/// the durable mutation state rather than guessing from whatever message page is visible.
	/// </summary>
	Task<IReadOnlyList<Guid>> GetTerminalMutationIds(IReadOnlyList<Guid> mutationItemIds);

	Task<MessageBodyDto> GetMessageBody(Guid messageId);

	/// <summary>
	/// The meeting invite this message carries, if any (§13 Epic 7) — null for an ordinary
	/// message, and also null until the message's raw content has been fetched (this reads the
	/// same stored bytes <see cref="GetMessageBody"/> derives its content from, not a live
	/// re-fetch).
	/// </summary>
	Task<MessageInviteDto?> GetMessageInvite(Guid messageId);

	/// <summary>
	/// Applies a reply that could not be authenticated with DKIM/DMARC only after the user
	/// explicitly accepts the spoofing risk shown in the reading pane.
	/// </summary>
	Task AcceptUnverifiedInviteReply(Guid messageId);

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

	Task<IReadOnlyList<ContactDto>> GetContacts(Guid accountId);

	Task<IReadOnlyList<ContactSuggestionDto>> GetContactSuggestions(Guid accountId);

	Task<IReadOnlyList<ContactDto>> SearchContacts(Guid accountId, string query);

	Task<ContactDto> SaveContact(SaveContactRequest request);

	Task<ContactDto> ResolveContactConflict(Guid contactId, bool keepMine);

	Task DeleteContact(DeleteContactRequest request);

	Task AbandonAmbiguousContactCreate(Guid contactId);

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

	Task<MutationEnqueueResultDto> MoveMessages(
		Guid accountId,
		IReadOnlyList<Guid> messageIds,
		Guid targetMailboxId
	);

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
	Task<MutationEnqueueResultDto> RemoveFromMailbox(
		Guid accountId,
		IReadOnlyList<Guid> messageIds,
		Guid mailboxId
	);

	Task<MutationEnqueueResultDto> MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds);

	/// <summary>Deletes the message outright — never reversible by the app (§6).</summary>
	Task<MutationEnqueueResultDto> DeletePermanently(
		Guid accountId,
		IReadOnlyList<Guid> messageIds
	);

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
	/// Resolves the canonical account, mailbox, message and rendering context for a notification
	/// click. A staged Gmail arrival returns <see cref="NotificationNavigationStatus.Pending"/>
	/// until canonical replay can safely materialise it; null means the durable notification or
	/// its message no longer exists.
	/// </summary>
	Task<NotificationNavigationDto?> ResolveNotificationNavigation(Guid notificationId);

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
	ContactService contacts,
	MailboxManagement mailboxes,
	CalendarEventService calendarEvents,
	Notifications.NotificationService notifications,
	ChangeStreamService changeStream,
	IMailProviderFactory providers,
	Scheduling.ExportJobs export,
	ITrustedCertificateStore certificates,
	IBackgroundJobClient jobs,
	Scheduling.ConnectivityMonitor connectivity,
	Scheduling.PollRegistry polls,
	Scheduling.ImapIdleRegistry imapIdle,
	MailInviteMaterializer invites,
	IIncomingMailAuthentication authentication,
	IHubEvents events,
	Scheduling.AccountGate gate
) : Hub<IMailClient>, IMailHub
{
	public Task<IReadOnlyList<MailboxSummaryDto>> GetMailboxes(Guid accountId) =>
		MailboxSummaryDtoFactory.ListAsync(context, accountId);

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
		var threadCounts = messages
			.GroupBy(ThreadKey, StringComparer.Ordinal)
			.ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
		return await ToMessageSummariesAsync(shown, threadCounts);
	}

	public async Task<IReadOnlyList<MessageSummaryDto>> GetThreadMessages(
		Guid mailboxId,
		string threadId
	)
	{
		var messages = await context
			.MessageMailboxes.Where(occurrence => occurrence.MailboxId == mailboxId)
			.Join(
				context.Messages.Where(message => message.ThreadId == threadId),
				occurrence => occurrence.MessageId,
				message => message.Id,
				(_, message) => message
			)
			.ToListAsync();
		messages.Sort((left, right) =>
		{
			var received = right.ReceivedAt.CompareTo(left.ReceivedAt);
			return received != 0 ? received : right.Id.CompareTo(left.Id);
		});
		var counts = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			[threadId] = messages.Count,
		};
		return await ToMessageSummariesAsync(messages, counts);
	}

	private async Task<IReadOnlyList<MessageSummaryDto>> ToMessageSummariesAsync(
		IReadOnlyList<Message> messages,
		IReadOnlyDictionary<string, int> threadCounts
	)
	{
		var failures = await MessageMutationFailures.ForMessagesAsync(
			context,
			messages.Select(message => message.Id).ToList()
		);
		return
		[
			.. messages.Select(message => new MessageSummaryDto(
				message.Id,
				message.AccountId,
				message.Subject,
				message.Snippet,
				message.From,
				message.ReceivedAt,
				message.IsRead,
				message.IsFlagged,
				message.HasNonInlineAttachments,
				failures.TryGetValue(message.Id, out var category) ? category : null,
				ThreadId: message.ThreadId
			)
			{
				ThreadMessageCount = threadCounts.GetValueOrDefault(ThreadKey(message), 1),
			}),
		];
	}

	private static string ThreadKey(Message message) =>
		message.ThreadId ?? message.Id.ToString();

	public Task SetActiveMailbox(Guid accountId, Guid mailboxId)
	{
		imapIdle.SetActiveMailbox(Context.ConnectionId, accountId, mailboxId);
		return Task.CompletedTask;
	}

	public override async Task OnDisconnectedAsync(Exception? exception)
	{
		imapIdle.RemoveConnection(Context.ConnectionId);
		await base.OnDisconnectedAsync(exception);
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

	public async Task<IReadOnlyList<Guid>> GetTerminalMutationIds(
		IReadOnlyList<Guid> mutationItemIds
	) =>
		await context
			.MutationItems.Where(i =>
				mutationItemIds.Contains(i.Id)
				&& (
					i.State == MutationState.Completed
					|| i.State == MutationState.Failed
					|| i.State == MutationState.Cancelled
				)
			)
			.Select(i => i.Id)
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

		var ics = await CalendarMimeReader.TryReadAsync(part, Context.ConnectionAborted);
		if (ics is null)
		{
			return null;
		}
		var method = CalDavIcs.ParseMethod(ics);
		if (!string.Equals(method, "REQUEST", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(method, "REPLY", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		var parsed = CalDavIcs.ParseEvents(ics, $"mail:{messageId}", string.Empty, 64).FirstOrDefault();
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

		if (string.Equals(method, "REPLY", StringComparison.OrdinalIgnoreCase))
		{
			var attendee = parsed.Attendees.FirstOrDefault();
			var authenticated = await authentication.VerifyAsync(mime, Context.ConnectionAborted);
			var requiresManualReview =
				!authenticated.IsAuthenticated
				|| attendee is null
				|| !string.Equals(authenticated.AuthenticatedAddress, attendee.Email, StringComparison.OrdinalIgnoreCase);
			return new MessageInviteDto(
				ev?.Id,
				parsed.Title,
				parsed.Start,
				parsed.End,
				parsed.IsAllDay,
				parsed.Organizer,
				null,
				true,
				attendee?.Email,
				attendee is null ? null : ToInviteResponse(attendee.ResponseStatus),
				requiresManualReview
			);
		}

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
			.Select(a => new AttachmentDto(a.Id, a.MessageId, a.Filename, a.MimeType, a.Size, a.IsInline, a.ContentId))
			.ToListAsync();

	public async Task<MessageReplyContextDto> GetMessageReplyContext(Guid messageId)
	{
		var message = await context.Messages.FirstAsync(m => m.Id == messageId);
		return new MessageReplyContextDto(
			message.Id,
			message.AccountId,
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
		if (mailbox.IsCollapsed == collapsed)
		{
			return;
		}

		mailbox.IsCollapsed = collapsed;
		await context.SaveChangesAsync();
		await MailboxSummaryDtoFactory.AnnounceManyAsync(context, events, [mailbox.Id]);
	}

	public async Task SetMailboxInitialSyncOverride(Guid mailboxId, InitialSyncMode? mode, int? boundValue)
	{
		if (mode is not null and not InitialSyncMode.Full && boundValue is not > 0)
		{
			throw new HubException("A bounded initial sync needs a positive month/message count.");
		}

		var overrideBound = mode is null or InitialSyncMode.Full ? null : boundValue;
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync();
			var updated = await context
				.Mailboxes.Where(mailbox => mailbox.Id == mailboxId)
				.ExecuteUpdateAsync(setters =>
					setters
						.SetProperty(mailbox => mailbox.InitialSyncModeOverride, mode)
						.SetProperty(mailbox => mailbox.InitialSyncBoundValueOverride, overrideBound)
						.SetProperty(
							mailbox => mailbox.CoveragePolicyGeneration,
							mailbox => mailbox.CoveragePolicyGeneration + 1
						)
				);
			if (updated == 0)
			{
				throw new InvalidOperationException($"Mailbox '{mailboxId}' does not exist.");
			}

			await context
				.MailboxCoverageStates.Where(coverage => coverage.MailboxId == mailboxId)
				.ExecuteUpdateAsync(setters =>
					setters
						.SetProperty(coverage => coverage.Status, CoverageStatus.NotStarted)
						.SetProperty(coverage => coverage.MessagesFetched, 0)
						.SetProperty(coverage => coverage.EstimatedTotal, (int?)null)
						.SetProperty(coverage => coverage.ResumeToken, (string?)null)
						.SetProperty(coverage => coverage.StartedAt, (DateTimeOffset?)null)
						.SetProperty(coverage => coverage.LastError, (string?)null)
				);
			await transaction.CommitAsync();
		});

		// After the commit: the coverage this reset is about to re-run from zero is on every
		// window's sidebar, not only the one that changed the bound.
		await MailboxSummaryDtoFactory.AnnounceManyAsync(context, events, [mailboxId]);
	}

	public async Task SetMailboxSpecialUseOverride(Guid mailboxId, SpecialUse? specialUse)
	{
		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == mailboxId);
		if (mailbox.SpecialUseOverride == specialUse)
		{
			return;
		}

		mailbox.SpecialUseOverride = specialUse;
		await context.SaveChangesAsync();
		await MailboxSummaryDtoFactory.AnnounceManyAsync(context, events, [mailbox.Id]);
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

		var accountChanged = await context.SaveChangesAsync() > 0;

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
			if (account.ProviderType != ProviderType.Imap)
				jobs.Enqueue<Scheduling.ContactJobs>(job => job.StartRefreshAsync(account.Id));
		}

		// Every open window shows this account's name, colour and sync settings; the window
		// that made the change is not the only one that has to stop showing the old ones
		// (§13 Epic 10). An idempotent write is not a transition to announce.
		if (accountChanged)
		{
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, default, gate);
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
		var reordered = new List<Account>();
		for (var index = 0; index < orderedAccountIds.Count; index++)
		{
			if (accounts.TryGetValue(orderedAccountIds[index], out var account) && account.SortOrder != index)
			{
				account.SortOrder = index;
				reordered.Add(account);
			}
		}

		await context.SaveChangesAsync();

		// Only the accounts that actually moved: a drag that ends where it started is not a
		// change, and announcing it would make an idempotent call indistinguishable from a
		// real reorder.
		foreach (var account in reordered)
		{
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, default, gate);
		}
	}

	public async Task SetAccountSidebarCollapsed(Guid accountId, bool collapsed)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == accountId);
		if (account.SidebarCollapsed == collapsed)
		{
			return;
		}

		account.SidebarCollapsed = collapsed;
		await context.SaveChangesAsync();
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, default, gate);
	}

	public Task TrustCertificate(Guid accountId, string hostname, string sha256Fingerprint) =>
		certificates.TrustAsync(accountId, hostname, sha256Fingerprint, default);

	public Task SetFlags(Guid accountId, IReadOnlyList<Guid> messageIds, bool? isRead, bool? isFlagged) =>
		EnqueueEachAsync(
			messageIds,
			messageId => mutations.SetFlagsAsync(accountId, messageId, new FlagUpdate(isRead, isFlagged))
		);

	public async Task<MutationEnqueueResultDto> MoveMessages(
		Guid accountId,
		IReadOnlyList<Guid> messageIds,
		Guid targetMailboxId
	)
	{
		// A synthesized mailbox (ProviderMailboxId null — a local nested Gmail-label-group
		// intermediate, no real label backing it) has no provider identity to receive a
		// message into. The renderer's own "Move to" menu and drag-and-drop already exclude
		// synthesized targets (§13 Epic 2), but a direct hub call bypasses that — without this
		// check the mutation would enqueue, dispatch, and only then hit
		// GmailMailProvider.ProviderMailboxId's internal assertion, which MutationExecutor's
		// generic catch treats as Ambiguous and retries — a deterministic failure that would
		// keep re-throwing identically forever, not a transient one reconciliation can resolve.
		// Rejecting before enqueue means nothing is ever queued for it to get stuck on.
		var target = await context.Mailboxes.FirstAsync(m => m.Id == targetMailboxId);
		if (target.ProviderMailboxId is null)
		{
			throw new HubException(
				"This is a nested label group, not a real Gmail label — move the message into one of the labels inside it instead."
			);
		}
		return await EnqueueEachWithResultAsync(
			messageIds,
			messageId => mutations.MoveAsync(accountId, messageId, targetMailboxId)
		);
	}

	public Task<MutationEnqueueResultDto> RemoveFromMailbox(
		Guid accountId,
		IReadOnlyList<Guid> messageIds,
		Guid mailboxId
	) =>
		EnqueueEachWithResultAsync(
			messageIds,
			messageId => mutations.RemoveFromMailboxAsync(accountId, messageId, mailboxId)
		);

	public Task<MutationEnqueueResultDto> MoveToTrash(Guid accountId, IReadOnlyList<Guid> messageIds) =>
		EnqueueEachWithResultAsync(messageIds, messageId => mutations.MoveToTrashAsync(accountId, messageId));

	public Task<MutationEnqueueResultDto> DeletePermanently(
		Guid accountId,
		IReadOnlyList<Guid> messageIds
	) =>
		EnqueueEachWithResultAsync(messageIds, messageId => mutations.DeletePermanentlyAsync(accountId, messageId));

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

	private static async Task<MutationEnqueueResultDto> EnqueueEachWithResultAsync(
		IReadOnlyList<Guid> messageIds,
		Func<Guid, Task<MutationItem>> enqueue
	)
	{
		var accepted = new List<MutationEnqueueAcceptanceDto>(messageIds.Count);
		var rejected = new List<Guid>();
		foreach (var messageId in messageIds)
		{
			try
			{
				var item = await enqueue(messageId);
				accepted.Add(new MutationEnqueueAcceptanceDto(messageId, item.Id));
			}
			catch
			{
				rejected.Add(messageId);
			}
		}
		return new MutationEnqueueResultDto(accepted, rejected);
	}

	public async Task<IReadOnlyList<ContactDto>> GetContacts(Guid accountId)
	{
		// The cache is the offline contract. Refresh work gets its own scope after this hub
		// call returns; it must never retain the connection-scoped DbContext.
		jobs.Enqueue<Scheduling.ContactJobs>(job => job.StartRefreshAsync(accountId));
		return await ToContactDtosAsync(
			await contacts.ListAsync(accountId, null, Context.ConnectionAborted)
		);
	}

	public async Task<IReadOnlyList<ContactSuggestionDto>> GetContactSuggestions(Guid accountId)
	{
		jobs.Enqueue<Scheduling.ContactJobs>(job => job.StartRefreshAsync(accountId));
		return (await contacts.ListSuggestionsAsync(accountId, Context.ConnectionAborted))
			.Select(suggestion => new ContactSuggestionDto(
				suggestion.DisplayName,
				suggestion.Emails.Select(email => new ContactAddressDto(email)).ToArray()
			))
			.ToArray();
	}

	public async Task<IReadOnlyList<ContactDto>> SearchContacts(Guid accountId, string query) =>
		await ToContactDtosAsync(
			await contacts.ListAsync(accountId, query, Context.ConnectionAborted)
		);

	public async Task<ContactDto> SaveContact(SaveContactRequest request)
	{
		var contact = await contacts.SaveAsync(
			new ContactInput(
				request.ContactId,
				request.AccountId,
				request.DisplayName,
				request.Emails,
				request.ExpectedRevision
			),
			Context.ConnectionAborted
		);
		return (await ToContactDtosAsync([contact])).Single();
	}

	public async Task<ContactDto> ResolveContactConflict(Guid contactId, bool keepMine)
	{
		var contact = await contacts.ResolveConflictAsync(
			contactId,
			keepMine,
			Context.ConnectionAborted
		);
		return (await ToContactDtosAsync([contact])).Single();
	}

	public Task DeleteContact(DeleteContactRequest request) =>
		contacts.DeleteAsync(request.ContactId, request.ExpectedRevision, Context.ConnectionAborted);
	public Task AbandonAmbiguousContactCreate(Guid contactId) =>
		contacts.AbandonAmbiguousCreateAsync(contactId, Context.ConnectionAborted);


	private async Task<IReadOnlyList<ContactDto>> ToContactDtosAsync(
		IReadOnlyList<Contact> contactsToMap
	)
	{
		var ids = contactsToMap.Select(contact => contact.Id).ToArray();
		var accountIds = contactsToMap.Select(contact => contact.AccountId).Distinct().ToArray();
		var providerTypes = await context.Accounts
			.Where(account => accountIds.Contains(account.Id))
			.ToDictionaryAsync(
				account => account.Id,
				account => account.ProviderType,
				Context.ConnectionAborted
			);
		var operations = await context.ContactOperations
			.Where(operation => ids.Contains(operation.ContactId))
			.Select(operation => new
			{
				operation.ContactId,
				operation.Sequence,
				operation.Kind,
				operation.State,
			})
			.ToListAsync(Context.ConnectionAborted);
		return contactsToMap.Select(contact =>
		{
			var contactOperations = operations
				.Where(operation => operation.ContactId == contact.Id)
				.OrderBy(operation => operation.Sequence)
				.ToArray();
			var states = contactOperations.Select(operation => operation.State).ToArray();
			var latestState = contactOperations.LastOrDefault()?.State;
			var ambiguousCreate = contactOperations.Any(operation =>
				operation.Kind == ContactOperationKind.Create
				&& operation.State is ContactOperationState.Dispatched
					or ContactOperationState.Ambiguous
			);
			var canDelete = !contact.SyncConflict
				&& !ambiguousCreate
				&& (providerTypes[contact.AccountId] != ProviderType.Gmail
					|| contact.ProviderContactId is null);
			return new ContactDto(
				contact.Id,
				contact.AccountId,
				contact.DisplayName,
				contact.Addresses.Select(address => new ContactAddressDto(address.Email)).ToArray(),
				contact.ProviderRevision,
				contact.SyncConflict,
				states.Any(state => state is ContactOperationState.Pending or ContactOperationState.Dispatched),
				states.Contains(ContactOperationState.Ambiguous),
				ambiguousCreate,
				latestState == ContactOperationState.Rejected,
				canDelete
			);
		}).ToArray();
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
				request.IsAllDay,
				request.StartTimeZoneId,
				request.EndTimeZoneId,
				request.RecurrenceRules,
				request.RecurrenceDates,
				request.ExceptionDates
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
			ev.RecurrenceRules.Count > 0 || ev.RecurrenceDates.Count > 0 || ev.ExceptionDates.Count > 0 || ev.RecurrenceMasterId != null,
			ev.SyncConflict,
			false,
			null,
			ev.RecurrenceRules.Count > 0 || ev.RecurrenceDates.Count > 0 || ev.ExceptionDates.Count > 0
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
			ev.IsAllDay,
			CalendarTimeZoneIds.Canonicalize(ev.StartTimeZoneId),
			CalendarTimeZoneIds.Canonicalize(ev.EndTimeZoneId),
			ev.RecurrenceRules,
			ev.RecurrenceDates,
			ev.ExceptionDates
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

	public async Task AcceptUnverifiedInviteReply(Guid messageId)
	{
		var raw = await context.MessageRaws.FirstOrDefaultAsync(row => row.MessageId == messageId);
		if (raw is null)
		{
			return;
		}
		var accountId = await context
			.Messages.Where(message => message.Id == messageId)
			.Select(message => message.AccountId)
			.SingleAsync();
		var account = await context.Accounts.SingleAsync(row => row.Id == accountId);
		using var stream = new MemoryStream(raw.Content);
		var mime = await MimeKit.MimeMessage.LoadAsync(stream, Context.ConnectionAborted);
		await invites.ApplyUnverifiedReplyAsync(account, mime, Context.ConnectionAborted);
	}

	public Task MarkNotificationDelivered(Guid notificationId) =>
		notifications.MarkDeliveredAsync(notificationId);

	public async Task<NotificationNavigationDto?> ResolveNotificationNavigation(Guid notificationId)
	{
		var record = await context
			.NotificationRecords.AsNoTracking()
			.FirstOrDefaultAsync(notification => notification.Id == notificationId);
		if (record is null)
		{
			return null;
		}

		var messageId = record.MessageId;
		if (messageId is null)
		{
			var account = await context.Accounts.FirstOrDefaultAsync(account => account.Id == record.AccountId);
			if (account is null || !account.IsEnabled)
			{
				return null;
			}

			// Draining the whole staged queue, not just this one notification's event: replay
			// only ever proceeds in order (§3), and the record this click is asking about is
			// already known to have been staged, so its event is somewhere in that queue.
			await changeStream.ReplayStagedAsync(account);
			var refreshedRecord = await context
				.NotificationRecords.AsNoTracking()
				.Where(notification => notification.Id == notificationId)
				.Select(notification => new { notification.MessageId })
				.FirstOrDefaultAsync();
			if (refreshedRecord is null)
			{
				return null;
			}
			messageId = refreshedRecord.MessageId;
			if (messageId is null)
			{
				if (await changeStream.CoverageCompleteAsync(account, default))
				{
					return null;
				}

				return new NotificationNavigationDto(
					NotificationNavigationStatus.Pending,
					record.AccountId,
					MailboxId: null,
					MessageId: null,
					Subject: string.Empty,
					SenderAddress: string.Empty
				);
			}
		}

		// Gmail can hold one canonical message in several labels. Resolve message metadata and
		// membership in one database statement so the route cannot combine context from one
		// local identity with a mailbox selected from another. Prefer the effective Inbox role,
		// then the user's stable sidebar order; provider occurrence identity is irrelevant.
		var route = await context
			.MessageMailboxes.AsNoTracking()
			.Where(occurrence => occurrence.MessageId == messageId)
			.Join(
				context.Mailboxes,
				occurrence => occurrence.MailboxId,
				mailbox => mailbox.Id,
				(occurrence, mailbox) => new { occurrence.MessageId, Mailbox = mailbox }
			)
			.Join(
				context.Messages.Where(message => message.AccountId == record.AccountId),
				item => item.MessageId,
				message => message.Id,
				(item, message) => new { Message = message, item.Mailbox }
			)
			.OrderByDescending(item => (item.Mailbox.SpecialUseOverride ?? item.Mailbox.SpecialUse) == SpecialUse.Inbox)
			.ThenBy(item => item.Mailbox.LocalSortOrder)
			.ThenBy(item => item.Mailbox.Id)
			.FirstOrDefaultAsync();
		if (route is null)
		{
			return null;
		}

		return new NotificationNavigationDto(
			NotificationNavigationStatus.Ready,
			route.Message.AccountId,
			route.Mailbox.Id,
			route.Message.Id,
			route.Message.Subject,
			route.Message.From.FirstOrDefault()?.Email ?? string.Empty
		);
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
