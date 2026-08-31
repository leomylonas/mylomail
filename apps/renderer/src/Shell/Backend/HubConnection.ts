import {
	HubConnectionBuilder,
	LogLevel,
	type HubConnection,
} from "@microsoft/signalr";
import type { QueryClient } from "@tanstack/react-query";
import type Store from "react-granular-store";
import { present } from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import {
	notify,
	type NotificationState,
} from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import type { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";

/** Query keys, in one place so an event and the query it invalidates cannot drift apart. */
export const queryKeys = {
	mailboxes: (accountId: string) => ["mailboxes", accountId] as const,
	accountCapabilities: (accountId: string) =>
		["account-capabilities", accountId] as const,
	messages: (mailboxId: string) => ["messages", mailboxId] as const,
	pending: (accountId: string) => ["pending", accountId] as const,
	search: (accountId: string, query: string, mailboxId: string | null) =>
		["search", accountId, query, mailboxId] as const,
};

/**
 * Opens the hub and points its events at the query cache.
 *
 * SignalR events invalidate and update TanStack Query rather than feeding a second state
 * system: two caches of the same server state would disagree, and the one the UI happened to
 * read would decide what the user saw (§12).
 */
export function connectHub(
	queryClient: QueryClient,
	notifications: Store<NotificationState>,
): HubConnection {
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

	// New mail, a changed flag, or a deletion all mean the list is stale. Invalidating by
	// prefix rather than by mailbox because a message can belong to several at once, and the
	// event does not say which lists are showing it.
	for (const event of ["MessageReceived", "MessageUpdated", "MessageDeleted"]) {
		hub.on(event, () => {
			void queryClient.invalidateQueries({ queryKey: ["messages"] });
			void queryClient.invalidateQueries({ queryKey: ["search"] });
		});
	}

	// A change the user asked for that will not happen. Shown, not logged: the optimistic
	// state has already been reverted, so without this the flag springs back with no
	// explanation and the user is left believing the app is simply unreliable.
	hub.on(
		"MessageSyncFailed",
		(failure: {
			messageId: string;
			category: ErrorCategory;
			detail: string | null;
		}) => {
			const presentation = present(failure.category, failure.detail);
			notify(notifications, {
				kind: "error",
				title: presentation.title,
				detail: presentation.detail,
				action: presentation.action
					? {
							label: "Details",
							run: () =>
								void queryClient.invalidateQueries({ queryKey: ["messages"] }),
						}
					: undefined,
			});

			void queryClient.invalidateQueries({ queryKey: ["pending"] });
			void queryClient.invalidateQueries({ queryKey: ["messages"] });
		},
	);

	// A reconnect is a full resynchronisation, not a pending-mutation check. While
	// disconnected this window missed every event above, and pending mutations alone cannot
	// repair a cache that is now simply wrong (§7).
	hub.onreconnected(() => {
		void queryClient.invalidateQueries();
	});

	return hub;
}
