import {
	HubConnectionBuilder,
	LogLevel,
	type HubConnection,
} from "@microsoft/signalr";
import type { QueryClient } from "@tanstack/react-query";

/** Query keys, in one place so an event and the query it invalidates cannot drift apart. */
export const queryKeys = {
	mailboxes: (accountId: string) => ["mailboxes", accountId] as const,
	messages: (mailboxId: string) => ["messages", mailboxId] as const,
	pending: (accountId: string) => ["pending", accountId] as const,
};

/**
 * Opens the hub and points its events at the query cache.
 *
 * SignalR events invalidate and update TanStack Query rather than feeding a second state
 * system: two caches of the same server state would disagree, and the one the UI happened to
 * read would decide what the user saw (§12).
 */
export function connectHub(queryClient: QueryClient): HubConnection {
	const hub = new HubConnectionBuilder()
		// Relative: the page is served by the backend, so the handshake carries the httpOnly
		// launch cookie and no token appears in the URL (§9).
		.withUrl("/hub")
		.withAutomaticReconnect()
		// At Information SignalR logs negotiated URLs.
		.configureLogging(LogLevel.Warning)
		.build();

	hub.on("SyncProgress", (progress: { mailboxId: string }) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.messages(progress.mailboxId),
		});
	});

	// Adding or removing an account changes what every window can show, and the account list
	// is otherwise fetched once and never again.
	hub.on("AccountStatusChanged", () => {
		void queryClient.invalidateQueries({ queryKey: ["accounts"] });
	});

	hub.on("MailboxUpdated", (mailbox: { accountId: string }) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(mailbox.accountId),
		});
	});

	hub.on("MailboxTreeChanged", (accountId: string) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(accountId),
		});
	});

	for (const event of ["MessageReceived", "MessageUpdated"]) {
		hub.on(event, () => {
			void queryClient.invalidateQueries({ queryKey: ["messages"] });
		});
	}

	hub.on("MessageDeleted", () => {
		void queryClient.invalidateQueries({ queryKey: ["messages"] });
	});

	// A reconnect is a full resynchronisation, not a pending-mutation check. While
	// disconnected this window missed every event above, and pending mutations alone cannot
	// repair a cache that is now simply wrong (§7).
	hub.onreconnected(() => {
		void queryClient.invalidateQueries();
	});

	return hub;
}
