using MyloMail.Api.Contracts;
using TypedSignalR.Client;

namespace MyloMail.Api.Hubs;

/// <summary>
/// Server-to-client events (§7).
/// </summary>
/// <remarks>
/// <para>
/// Every event §7 lists is declared here even where nothing raises it yet, because §7's table
/// exists to pair each event with the mechanism that produces it. Declaring the full set keeps
/// that pairing checkable: an event with no producer is visible as a gap rather than as an
/// absence nobody notices.
/// </para>
/// <para>
/// <b>Reconnect is a full resynchronisation, not a pending-mutation check.</b> A disconnected
/// renderer misses everything below, and pending mutations cannot repair a stale cache — the
/// client invalidates and refetches its active queries on reconnect.
/// </para>
/// </remarks>
[Receiver]
public interface IMailClient
{
	Task AccountStatusChanged(AccountDto account);

	Task MailboxUpdated(MailboxSummaryDto mailbox);

	Task MailboxTreeChanged(Guid accountId);

	/// <summary>
	/// Steady-state sync only, <b>never</b> initial sync: notifying a user about a backlog
	/// they already had is a flood, not news (§13, Epic 9).
	/// </summary>
	Task MessageReceived(MessageSummaryDto message);

	Task MessageUpdated(MessageSummaryDto message);

	Task MessageDeleted(Guid messageId);

	Task SyncProgress(SyncProgressDto progress);

	Task ExportProgress(Guid exportId, int written, int total);

	/// <summary>A mutation item reaching terminal failure (§6).</summary>
	Task MessageSyncFailed(MutationFailureDto failure);

	Task DraftUpdated(Guid draftId);

	Task OutboxStatusChanged(OutboxItemDto item);

	Task CalendarEventUpdated(Guid eventId);

	Task CalendarConflictDetected(Guid eventId);
	Task ContactsChanged(Guid accountId);


	/// <summary>
	/// Emitted when network-class failures begin and when connectivity returns, so the UI can
	/// show one calm offline state rather than per-mailbox errors multiplying every poll (§15).
	/// </summary>
	Task ConnectivityChanged(bool online);

	/// <summary>App-wide theme/close-behaviour/mailto-prompt state changed (§7, §13 Epic 10).</summary>
	Task ShellSettingsChanged();

	/// <summary>The remote-content sender/domain rules changed (§7, §13 Epic 5, Epic 10).</summary>
	Task RemoteContentRulesChanged();

	/// <summary>
	/// Relayed straight to the preload bridge, which asks <c>electron-shell</c> to show the
	/// native OS notification. Dispatch is the shell's job, not the renderer's (§13 Epic 9).
	/// </summary>
	Task NotificationReady(NotificationDto notification);
}
