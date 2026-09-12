import {
	HubConnectionBuilder,
	LogLevel,
	type HubConnection,
} from "@microsoft/signalr";
import type { InfiniteData, QueryClient } from "@tanstack/react-query";
import type Store from "react-granular-store";
import { present } from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import {
	optimisticMutationIds,
	settleOptimisticMutation,
	settleOptimisticMutations,
} from "@mylomail/renderer/Shell/Backend/OptimisticMessageState";
import { normalizeHubErrors } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import {
	notify,
	type NotificationState,
} from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import type { WindowState } from "@mylomail/renderer/Shell/WindowScope/WindowStore";
import type {
	MessageSummaryDto,
	MutationFailureDto,
	MutationSettledDto,
	NotificationDto,
	SyncProgressDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import type { SyncProgressKind } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

/** Query keys, in one place so an event and the query it invalidates cannot drift apart. */
export const queryKeys = {
	mailboxes: (accountId: string) => ["mailboxes", accountId] as const,
	accountCapabilities: (accountId: string) =>
		["account-capabilities", accountId] as const,
	messages: (mailboxId: string) => ["messages", mailboxId] as const,
	threadMessages: (mailboxId: string, threadId: string) =>
		["messages", mailboxId, "thread", threadId] as const,
	pending: (accountId: string) => ["pending", accountId] as const,
	search: (accountId: string, query: string, mailboxId: string | null) =>
		["search", accountId, query, mailboxId] as const,
	calendars: (accountId: string) => ["calendars", accountId] as const,
	calendarEvents: (calendarId: string, from: string, to: string) =>
		["calendar-events", calendarId, from, to] as const,
	contacts: (accountId: string, query = "") =>
		["contacts", accountId, query] as const,
	/**
	 * Cache-only: written by the `SyncProgress` event below, never fetched (§13 Epic 3).
	 *
	 * Keyed by kind as well as mailbox: backfill and content indexing both report here, and
	 * indexing continues after coverage completes, so one key would have the second producer
	 * overwrite the first and a finished backfill would appear to restart.
	 */
	syncProgress: (mailboxId: string, kind: SyncProgressKind) =>
		["sync-progress", mailboxId, kind] as const,
	/**
	 * Seeded by `GetConnectivity` on mount (so a window opened while already offline shows
	 * the calm banner immediately), then kept current by the `ConnectivityChanged` event
	 * below, which only fires on a transition (§7, §15).
	 */
	connectivity: () => ["connectivity"] as const,
	/**
	 * Cache-only, never fetched: written by `MessageSyncFailed` below when a mutation fails
	 * for a reason a reauthenticate/trust-certificate flow can actually fix, so `AppShell` can
	 * open that flow for the right account without `HubConnection` needing to reach into
	 * shell-level UI state directly (§15).
	 */
	reauthRequestedAccountId: () => ["reauth-requested-account"] as const,
};

/**
 * Message events carry confirmed flags and may also carry the first body-derived IMAP
 * snippet. Snippets only transition blank-to-populated; retaining a populated value prevents
 * an older concurrent mutation event from erasing it while still allowing the content event
 * itself to fill the list immediately.
 */
function updateMessageSummaryCaches(
	queryClient: QueryClient,
	message: MessageSummaryDto,
): void {
	queryClient.setQueriesData<InfiniteData<MessageSummaryDto[]>>(
		{
			queryKey: ["messages"],
			predicate: (query) => query.queryKey.length === 2,
		},
		(current) =>
			current && {
				...current,
				pages: current.pages.map((page) =>
					page.map((candidate) =>
						candidate.id === message.id
							? mergeServerKnownProjection(candidate, message)
							: candidate,
					),
				),
			},
	);
	queryClient.setQueriesData<MessageSummaryDto[]>(
		{
			queryKey: ["messages"],
			predicate: (query) => query.queryKey.length > 2,
		},
		(current) =>
			current?.map((candidate) =>
				candidate.id === message.id
					? mergeServerKnownProjection(candidate, message)
					: candidate,
			),
	);
	queryClient.setQueriesData<MessageSummaryDto[]>(
		{ queryKey: ["search"] },
		(current) =>
			current?.map((candidate) =>
				candidate.id === message.id
					? mergeServerKnownProjection(candidate, message)
					: candidate,
			),
	);
}

export function mergeServerKnownProjection(
	current: MessageSummaryDto,
	confirmed: MessageSummaryDto,
): MessageSummaryDto {
	return {
		...current,
		snippet: confirmed.snippet || current.snippet,
		isRead: confirmed.isRead,
		isFlagged: confirmed.isFlagged,
		mutationFailure: confirmed.mutationFailure,
	};
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
	windowStore: Store<WindowState>,
): HubConnection {
	const hub = new HubConnectionBuilder()
		// Relative: the page is served by the backend, so the handshake carries the httpOnly
		// launch cookie and no token appears in the URL (§9).
		.withUrl("/hub")
		.withAutomaticReconnect()
		// At Information SignalR logs negotiated URLs.
		.configureLogging(LogLevel.Warning)
		.build();
	normalizeHubErrors(hub);

	hub.on("SyncProgress", (progress: SyncProgressDto) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.messages(progress.mailboxId),
		});
		// Cache-only write, never fetched: the mailbox tree's progress indicator reads this
		// key with `enabled: false`, so it renders whatever this last wrote and nothing more
		// (§13 Epic 3) — there is no request that would ever produce this value on its own.
		queryClient.setQueryData(
			queryKeys.syncProgress(progress.mailboxId, progress.kind),
			progress,
		);
	});

	// Account health contributes to every mailbox's availability as well as the account row,
	// so both caches move together in every open window.
	hub.on("AccountStatusChanged", (account: { id: string }) => {
		void queryClient.invalidateQueries({ queryKey: ["accounts"] });
		void queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(account.id),
		});
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
	hub.on("ContactsChanged", (accountId: string) => {
		void queryClient.invalidateQueries({
			queryKey: ["contacts", accountId],
		});
	});

	// New mail, a changed flag, or a deletion all mean the list is stale. Invalidating by
	// prefix rather than by mailbox because a message can belong to several at once, and the
	// event does not say which lists are showing it.
	//
	// The pending projection goes with them: a mutation job confirming, cancelling or
	// reverting an intent removes its `MessagePendingChanges` row and announces the message,
	// and only the window that started it learns that from its own mutation call — every
	// other window would keep rendering the optimistic badge indefinitely (§7, Epic 10).
	for (const event of ["MessageReceived", "MessageUpdated", "MessageDeleted"]) {
		hub.on(event, (payload: MessageSummaryDto | string) => {
			if (event === "MessageUpdated" && typeof payload !== "string")
				updateMessageSummaryCaches(queryClient, payload);
			void Promise.all([
				queryClient.invalidateQueries({ queryKey: ["messages"] }),
				queryClient.invalidateQueries({ queryKey: ["search"] }),
				queryClient.invalidateQueries({ queryKey: ["pending"] }),
			]);
		});
	}

	hub.on("MessageMutationSettled", (settlement: MutationSettledDto) => {
		const releaseProjection = () =>
			settleOptimisticMutation(queryClient, settlement);
		void Promise.all([
			queryClient.invalidateQueries({ queryKey: ["messages"] }),
			queryClient.invalidateQueries({ queryKey: ["search"] }),
			queryClient.invalidateQueries({ queryKey: ["pending"] }),
		]).then(releaseProjection, releaseProjection);
	});

	// GetMessageBody's own existence check exists specifically so a reading pane left open on
	// a message that's since been deleted reports "no longer exists" rather than polling a
	// blank body forever (see its own comment) — but that guard is useless if the pane's query
	// never runs again once a body is first fetched successfully (ReadingPane's own
	// refetchInterval correctly stops polling a fetched body, since one never changes on its
	// own). Nothing else invalidates ["body", messageId] on deletion, so a currently-open
	// reading pane would otherwise keep showing a deleted message's stale content indefinitely,
	// discoverable only by reselecting it. Body content itself is immutable once fetched, so
	// this deliberately only refetches on deletion, not on every MessageReceived/MessageUpdated.
	// Scoped to the deleted message's own key — MessageDeleted carries a messageId, so there is
	// no reason to force every other currently-open reading pane to refetch its own body too.
	hub.on("MessageDeleted", (messageId: string) => {
		void queryClient.invalidateQueries({ queryKey: ["body", messageId] });
	});

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

	// Theme/close-behaviour/mailto-prompt and the remote-content rules (§13 Epics 8, 5)
	// changed in some window — every window converges per Epic 10's "all actions reflected
	// live across all open windows." Panel layout/window bounds are deliberately excluded
	// upstream (a read-once-at-open default, not something every window syncs to).
	hub.on("ShellSettingsChanged", () => {
		void queryClient.invalidateQueries({ queryKey: ["shell-settings"] });
	});

	hub.on("RemoteContentRulesChanged", () => {
		void queryClient.invalidateQueries({
			queryKey: ["remote-content-rules"],
		});
	});

	// A change the user asked for that will not happen. Shown, not logged: the optimistic
	// state has already been reverted, so without this the flag springs back with no
	// explanation and the user is left believing the app is simply unreliable.
	hub.on("MessageSyncFailed", (failure: MutationFailureDto) => {
		settleOptimisticMutation(queryClient, failure);
		const presentation = present(
			failure.category,
			failure.detail,
			failure.certificateHostname && failure.certificateSha256Fingerprint
				? {
						hostname: failure.certificateHostname,
						sha256Fingerprint: failure.certificateSha256Fingerprint,
					}
				: undefined,
		);
		// "reauthenticate" and "trust-certificate" both route to the same dialog:
		// ReauthenticateAccount already has its own internal trust-certificate flow,
		// triggered when a blank-password retry hits the same rejected certificate — so
		// there is nothing further to build for the cert case specifically, only a way to
		// open that dialog for the account this failure actually belongs to (§15).
		const opensReauthenticate =
			presentation.action === "reauthenticate" ||
			presentation.action === "trust-certificate";
		notify(notifications, {
			kind: "error",
			title: presentation.title,
			detail: presentation.detail,
			action: presentation.action
				? {
						label: opensReauthenticate ? "Reauthenticate" : "Details",
						run: () => {
							if (opensReauthenticate) {
								queryClient.setQueryData(
									queryKeys.reauthRequestedAccountId(),
									failure.accountId,
								);
							}
							void queryClient.invalidateQueries({ queryKey: ["messages"] });
						},
					}
				: undefined,
		});

		void queryClient.invalidateQueries({ queryKey: ["pending"] });
		void queryClient.invalidateQueries({ queryKey: ["messages"] });
	});

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
	hub.on("NotificationReady", (notification: NotificationDto) => {
		void window.notifications
			?.show({
				id: notification.id,
				accountId: notification.accountId,
				title: notification.title,
				body: notification.body,
			})
			.then(() => hub.invoke("MarkNotificationDelivered", notification.id))
			.catch((error: unknown) => {
				// Left unmarked-delivered on purpose: per the comment above, that's exactly
				// what makes the shell redeliver this same notification instead of losing
				// it. Logged only so a show/deliver failure doesn't surface as an unhandled
				// promise rejection with no trace of what happened.
				console.error(`notification delivery failed: ${String(error)}`);
			});
	});

	// A reconnect is a full resynchronisation, not just a pending-mutation check. While
	// disconnected this window missed every event above, so ordinary server-backed queries
	// are invalidated. Membership projections are deliberately cache-only, however: ask the
	// durable mutation table which exact claims became terminal while disconnected, preserving
	// claims that are still pending and releasing only outcomes the server can prove.
	hub.onreconnected(async () => {
		const mutationIds = optimisticMutationIds(queryClient);
		const terminalIds =
			mutationIds.length === 0
				? Promise.resolve<string[]>([])
				: hub
						.invoke<string[]>("GetTerminalMutationIds", mutationIds)
						.catch((error: unknown) => {
							console.error(
								`optimistic mutation reconciliation failed: ${String(error)}`,
							);
							return [];
						});
		const accountId = windowStore.getState("selectedAccountId");
		const mailboxId = windowStore.getState("selectedMailboxId");
		if (accountId && mailboxId) {
			void hub.invoke("SetActiveMailbox", accountId, mailboxId);
		}
		// Keep successful removals hidden until the stale source page has finished refetching.
		// Clearing first would briefly resurrect its old row before that response replaced it.
		try {
			await queryClient.invalidateQueries();
			settleOptimisticMutations(queryClient, await terminalIds);
		} catch (error) {
			console.error(`reconnect cache refresh failed: ${String(error)}`);
		}
	});

	return hub;
}
