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
	calendars: (accountId: string) => ["calendars", accountId] as const,
	calendarEvents: (calendarId: string, from: string, to: string) =>
		["calendar-events", calendarId, from, to] as const,
	/** Cache-only: written by the `SyncProgress` event below, never fetched (§13 Epic 3). */
	syncProgress: (mailboxId: string) => ["sync-progress", mailboxId] as const,
	/**
	 * Seeded by `GetConnectivity` on mount (so a window opened while already offline shows
	 * the calm banner immediately), then kept current by the `ConnectivityChanged` event
	 * below, which only fires on a transition (§7, §15).
	 */
	connectivity: () => ["connectivity"] as const,
};

export interface SyncProgress {
	mailboxId: string;
	messagesFetched: number;
	estimatedTotal: number | null;
}

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

	hub.on("SyncProgress", (progress: SyncProgress) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.messages(progress.mailboxId),
		});
		// Cache-only write, never fetched: the mailbox tree's progress indicator reads this
		// key with `enabled: false`, so it renders whatever this last wrote and nothing more
		// (§13 Epic 3) — there is no request that would ever produce this value on its own.
		queryClient.setQueryData(
			queryKeys.syncProgress(progress.mailboxId),
			progress,
		);
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

	// A draft created, saved, deleted, pushed to the server, or materialised locally by sync
	// (§7) — the event only carries draftId, not accountId, so this invalidates by prefix
	// like the message events above rather than trying to scope it.
	hub.on("DraftUpdated", () => {
		void queryClient.invalidateQueries({ queryKey: ["drafts"] });
	});

	// One calm offline state rather than per-mailbox error noise (§7, §15) — written
	// directly rather than invalidated, the same cache-only pattern SyncProgress uses above,
	// since there is nothing to refetch: the event already carries the new value.
	hub.on("ConnectivityChanged", (online: boolean) => {
		queryClient.setQueryData(queryKeys.connectivity(), online);
	});

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

	// Neither event names the calendar it belongs to, only the event id, so this invalidates
	// broadly rather than trying to scope it — calendar volume is nowhere near mail volume.
	// Three surfaces can each show one event's current state (the calendar grid/agenda, the
	// EventModal detail view, and a message's InviteBanner) under three different query key
	// prefixes; staleTime: Infinity means none of them ever refetch on their own, so a change
	// from any one surface — an RSVP, a conflict, an organiser's update arriving via sync —
	// has to be pushed to all three explicitly, not just the one that triggered it.
	for (const event of ["CalendarEventUpdated", "CalendarConflictDetected"]) {
		hub.on(event, () => {
			void queryClient.invalidateQueries({ queryKey: ["calendar-events"] });
			void queryClient.invalidateQueries({
				queryKey: ["calendar-event-detail"],
			});
			void queryClient.invalidateQueries({ queryKey: ["invite"] });
		});
	}

	// Dispatch is the shell's job, not this window's (§13 Epic 9): relay straight to the
	// preload bridge, and confirm delivery only once the shell has actually shown it — a
	// crash between these two steps redelivers the same notification rather than losing it.
	hub.on(
		"NotificationReady",
		(notification: {
			id: string;
			accountId: string;
			messageId: string | null;
			title: string;
			body: string;
		}) => {
			void window.notifications
				?.show(notification)
				.then(() => hub.invoke("MarkNotificationDelivered", notification.id));
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
