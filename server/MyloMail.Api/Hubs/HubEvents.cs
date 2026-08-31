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

	Task AccountStatusChangedAsync(AccountDto account);
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

	public Task MessageReceivedAsync(MessageSummaryDto message) => hub.Clients.All.MessageReceived(message);

	public Task MessageUpdatedAsync(MessageSummaryDto message) => hub.Clients.All.MessageUpdated(message);

	public Task MessageDeletedAsync(Guid messageId) => hub.Clients.All.MessageDeleted(messageId);

	public Task AccountStatusChangedAsync(AccountDto account) => hub.Clients.All.AccountStatusChanged(account);
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

	public Task MessageReceivedAsync(MessageSummaryDto message) => Task.CompletedTask;

	public Task MessageUpdatedAsync(MessageSummaryDto message) => Task.CompletedTask;

	public Task MessageDeletedAsync(Guid messageId) => Task.CompletedTask;

	public Task AccountStatusChangedAsync(AccountDto account) => Task.CompletedTask;
}
