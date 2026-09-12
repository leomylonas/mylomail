using Microsoft.AspNetCore.SignalR;
using MyloMail.Api.Contracts;

namespace MyloMail.Api.Hubs;

/// <summary>
/// How the rest of the backend raises §7 events.
/// </summary>
/// <remarks>
/// An interface rather than <c>IHubContext</c> passed around directly, so sync and mutation
/// services depend on "something happened" rather than on SignalR. Those services are the ones
/// under fault-injection test, and a transport dependency in them would have to be stubbed in
/// every harness.
/// </remarks>
public interface IHubEvents
{
	Task SyncProgressAsync(SyncProgressDto progress);

	Task MailboxUpdatedAsync(MailboxSummaryDto mailbox);

	Task MailboxTreeChangedAsync(Guid accountId);

	Task OutboxStatusChangedAsync(OutboxItemDto item);

	Task MessageSyncFailedAsync(MutationFailureDto failure);

	Task MessageMutationSettledAsync(MutationSettledDto settlement);

	/// <summary>
	/// New mail. <b>Steady-state sync only.</b>
	/// </summary>
	/// <remarks>
	/// Never raised for initial sync: a new account's backlog is not news, and announcing
	/// thousands of messages the user already had is the flood §13 Epic 9 exists to prevent.
	/// The caller decides, because only it knows which kind of sync it is running.
	/// </remarks>
	Task MessageReceivedAsync(MessageSummaryDto message);

	Task MessageUpdatedAsync(MessageSummaryDto message);

	Task MessageDeletedAsync(Guid messageId);

	Task DraftUpdatedAsync(Guid draftId);

	/// <summary>Raised only after a calendar event is durably created, updated or removed.</summary>
	Task CalendarEventUpdatedAsync(Guid eventId);

	/// <summary>Raised when the provider rejects an event update's revision precondition.</summary>
	Task CalendarConflictDetectedAsync(Guid eventId);
	Task ContactsChangedAsync(Guid accountId);


	Task AccountStatusChangedAsync(AccountDto account);

	/// <summary>
	/// A durable notification record exists and is not yet delivered. Dispatch is owned by
	/// <c>electron-shell</c>; the renderer relays this straight to the preload bridge (§13
	/// Epic 9).
	/// </summary>
	Task NotificationReadyAsync(NotificationDto notification);

	/// <summary>After each batch a bulk-export job writes; also signals completion/cancellation.</summary>
	Task ExportProgressAsync(Guid exportId, int written, int total);

	/// <summary>
	/// Emitted when network-class failures begin and when connectivity returns, so the UI can
	/// show one calm offline state rather than per-mailbox errors multiplying every poll (§15).
	/// </summary>
	Task ConnectivityChangedAsync(bool online);

	/// <summary>
	/// Theme, close behaviour, or the mailto-prompt dismissal changed — the app-wide settings
	/// (§13 Epic 8) other than panel layout/window bounds, which are deliberately a
	/// read-once-at-open default rather than something every open window converges on
	/// (§12, <see cref="Controllers.AppSettingsController"/>'s own doc comment). Epic 10
	/// requires actions reflected live across every window; this is that requirement for the
	/// shell-settings singleton row.
	/// </summary>
	Task ShellSettingsChangedAsync();

	/// <summary>
	/// The remote-content sender/domain allow/block rules (§13 Epic 5) changed. Every open
	/// window must immediately re-evaluate the message it is displaying.
	/// </summary>
	Task RemoteContentRulesChangedAsync();
}

/// <summary>Broadcasts to every connected renderer.</summary>
/// <remarks>
/// Broadcast rather than per-connection groups because every window shows the same local
/// database; multi-window is an assumption here, and a window that filtered events by its own
/// subscription would have to re-derive what it missed on every navigation.
/// </remarks>
public sealed class HubEvents(IHubContext<MailHub, IMailClient> hub) : IHubEvents
{
	public Task SyncProgressAsync(SyncProgressDto progress) => hub.Clients.All.SyncProgress(progress);

	public Task MailboxUpdatedAsync(MailboxSummaryDto mailbox) => hub.Clients.All.MailboxUpdated(mailbox);

	public Task MailboxTreeChangedAsync(Guid accountId) => hub.Clients.All.MailboxTreeChanged(accountId);

	public Task OutboxStatusChangedAsync(OutboxItemDto item) => hub.Clients.All.OutboxStatusChanged(item);

	public Task MessageSyncFailedAsync(MutationFailureDto failure) =>
		hub.Clients.All.MessageSyncFailed(failure);

	public Task MessageMutationSettledAsync(MutationSettledDto settlement) =>
		hub.Clients.All.MessageMutationSettled(settlement);

	public Task MessageReceivedAsync(MessageSummaryDto message) => hub.Clients.All.MessageReceived(message);

	public Task MessageUpdatedAsync(MessageSummaryDto message) => hub.Clients.All.MessageUpdated(message);

	public Task MessageDeletedAsync(Guid messageId) => hub.Clients.All.MessageDeleted(messageId);

	public Task DraftUpdatedAsync(Guid draftId) => hub.Clients.All.DraftUpdated(draftId);

	public Task CalendarEventUpdatedAsync(Guid eventId) => hub.Clients.All.CalendarEventUpdated(eventId);

	public Task CalendarConflictDetectedAsync(Guid eventId) => hub.Clients.All.CalendarConflictDetected(eventId);
	public Task ContactsChangedAsync(Guid accountId) => hub.Clients.All.ContactsChanged(accountId);


	public Task AccountStatusChangedAsync(AccountDto account) => hub.Clients.All.AccountStatusChanged(account);

	public Task NotificationReadyAsync(NotificationDto notification) =>
		hub.Clients.All.NotificationReady(notification);

	public Task ExportProgressAsync(Guid exportId, int written, int total) =>
		hub.Clients.All.ExportProgress(exportId, written, total);

	public Task ConnectivityChangedAsync(bool online) => hub.Clients.All.ConnectivityChanged(online);

	public Task ShellSettingsChangedAsync() => hub.Clients.All.ShellSettingsChanged();

	public Task RemoteContentRulesChangedAsync() => hub.Clients.All.RemoteContentRulesChanged();
}

/// <summary>
/// Used where no renderer is listening — tests, and any host that runs jobs without a hub.
/// </summary>
public sealed class NoHubEvents : IHubEvents
{
	public Task SyncProgressAsync(SyncProgressDto progress) => Task.CompletedTask;

	public Task MailboxUpdatedAsync(MailboxSummaryDto mailbox) => Task.CompletedTask;

	public Task MailboxTreeChangedAsync(Guid accountId) => Task.CompletedTask;

	public Task OutboxStatusChangedAsync(OutboxItemDto item) => Task.CompletedTask;

	public Task MessageSyncFailedAsync(MutationFailureDto failure) => Task.CompletedTask;

	public Task MessageMutationSettledAsync(MutationSettledDto settlement) => Task.CompletedTask;

	public Task MessageReceivedAsync(MessageSummaryDto message) => Task.CompletedTask;

	public Task MessageUpdatedAsync(MessageSummaryDto message) => Task.CompletedTask;

	public Task MessageDeletedAsync(Guid messageId) => Task.CompletedTask;

	public Task DraftUpdatedAsync(Guid draftId) => Task.CompletedTask;

	public Task CalendarEventUpdatedAsync(Guid eventId) => Task.CompletedTask;

	public Task CalendarConflictDetectedAsync(Guid eventId) => Task.CompletedTask;
	public Task ContactsChangedAsync(Guid accountId) => Task.CompletedTask;


	public Task AccountStatusChangedAsync(AccountDto account) => Task.CompletedTask;

	public Task NotificationReadyAsync(NotificationDto notification) => Task.CompletedTask;

	public Task ExportProgressAsync(Guid exportId, int written, int total) => Task.CompletedTask;

	public Task ConnectivityChangedAsync(bool online) => Task.CompletedTask;

	public Task ShellSettingsChangedAsync() => Task.CompletedTask;

	public Task RemoteContentRulesChangedAsync() => Task.CompletedTask;
}
