import {
	HubConnectionBuilder,
	LogLevel,
	type HubConnection,
} from "@microsoft/signalr";
import type { InfiniteData, QueryClient } from "@tanstack/react-query";
import type Store from "react-granular-store";
import { present } from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import {
	hideOptimisticMessages,
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
	IMailClient,
	IMailHub,
} from "@mylomail/shared-types/SignalR/TypedSignalR.Client/MyloMail.Api.Hubs";
import {
	getHubProxyFactory,
	getReceiverRegister,
	type Disposable,
} from "@mylomail/shared-types/SignalR/TypedSignalR.Client";
import type {
	MessageSummaryDto,
	MutationFailureDto,
	MutationQueuedDto,
	MutationSettledDto,
	NotificationDto,
	SyncProgressDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import type { SyncProgressKind } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

export type MailHubConnection = HubConnection &
	IMailHub & {
		subscribe<K extends keyof IMailClient>(
			event: K,
			handler: (...args: Parameters<IMailClient[K]>) => void | Promise<void>,
		): Disposable;
	};

function createMailHubConnection(connection: HubConnection): MailHubConnection {
	normalizeHubErrors(connection);
	const listeners = new Map<
		keyof IMailClient,
		Set<(...args: unknown[]) => void | Promise<void>>
	>();
	const receiver = new Proxy(
		{},
		{
			get:
				(_, event: keyof IMailClient) =>
				(...args: unknown[]) =>
					Promise.all(
						[...(listeners.get(event) ?? [])].map((handler) =>
							handler(...args),
						),
					).then(() => undefined),
		},
	) as IMailClient;
	getReceiverRegister("IMailClient").register(connection, receiver);
	const proxy = getHubProxyFactory("IMailHub").createHubProxy(connection);
	// TypedSignalR's generated proxy also has an enumerable private `connection` field.
	// Copying that field onto HubConnection replaces SignalR's underlying HttpConnection
	// with the HubConnection itself, so start() recursively calls start() and fails in the
	// Connecting state. Only generated invocation functions belong on the public adapter.
	const hubMethods = Object.fromEntries(
		Object.entries(proxy).filter(([, value]) => typeof value === "function"),
	) as unknown as IMailHub;

	return Object.assign(connection, hubMethods, {
		subscribe<K extends keyof IMailClient>(
			event: K,
			handler: (...args: Parameters<IMailClient[K]>) => void | Promise<void>,
		): Disposable {
			const handlers = listeners.get(event) ?? new Set();
			handlers.add(handler as (...args: unknown[]) => void | Promise<void>);
			listeners.set(event, handlers);
			return {
				dispose: () =>
					handlers.delete(
						handler as (...args: unknown[]) => void | Promise<void>,
					),
			};
		},
	});
}

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
	drafts: (accountId: string) => ["drafts", accountId] as const,
	attachmentConstraints: (accountId: string) =>
		["attachmentConstraints", accountId] as const,

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
 * Removes server state that cannot be meaningful after an account disappears, without
 * disturbing another account a different window may still be viewing.
 */
export function removeAccountCaches(
	queryClient: QueryClient,
	accountId: string,
): void {
	const mailboxIds = new Set(
		(
			(queryClient.getQueryData(queryKeys.mailboxes(accountId)) as
				{ id: string }[] | undefined) ?? []
		).map((mailbox) => mailbox.id),
	);
	const messageIds = new Set<string>();

	for (const query of queryClient.getQueryCache().findAll({
		queryKey: ["messages"],
	})) {
		const mailboxId = query.queryKey[1];
		if (typeof mailboxId !== "string" || !mailboxIds.has(mailboxId)) continue;
		collectMessageIds(query.state.data, messageIds);
	}

	queryClient.removeQueries({ queryKey: queryKeys.mailboxes(accountId) });
	queryClient.removeQueries({
		queryKey: queryKeys.accountCapabilities(accountId),
	});
	queryClient.removeQueries({ queryKey: ["pending", accountId] });
	queryClient.removeQueries({ queryKey: ["contacts", accountId] });
	queryClient.removeQueries({ queryKey: ["calendars", accountId] });
	queryClient.removeQueries({ queryKey: queryKeys.drafts(accountId) });
	queryClient.removeQueries({
		queryKey: queryKeys.attachmentConstraints(accountId),
	});
	queryClient.removeQueries({
		queryKey: ["search", accountId],
	});
	queryClient.removeQueries({
		queryKey: ["messages"],
		predicate: (query) =>
			typeof query.queryKey[1] === "string" &&
			mailboxIds.has(query.queryKey[1]),
	});
	for (const messageId of messageIds) {
		queryClient.removeQueries({ queryKey: ["body", messageId] });
	}
}

function collectMessageIds(value: unknown, target: Set<string>): void {
	if (!value || typeof value !== "object") return;
	if (Array.isArray(value)) {
		for (const item of value) collectMessageIds(item, target);
		return;
	}
	const record = value as Record<string, unknown>;
	if (typeof record.id === "string" && typeof record.accountId === "string") {
		target.add(record.id);
	}
	if (Array.isArray(record.pages)) collectMessageIds(record.pages, target);
}

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

export function dispatchNativeNotification(
	hub: Pick<IMailHub, "markNotificationDelivered">,
	notification: NotificationDto,
	bridge: Window["notifications"],
): void {
	if (!bridge) {
		// Browser/attach renderers deliberately have no native bridge. Leave the durable
		// row pending so a later real shell connection can replay and acknowledge it.
		return;
	}
	void bridge
		.show({
			id: notification.id,
			accountId: notification.accountId,
			title: notification.title,
			body: notification.body,
		})
		.then(() => hub.markNotificationDelivered(notification.id))
		.catch((error: unknown) => {
			// Left unmarked-delivered on purpose: that's exactly what makes the shell
			// redeliver this notification instead of losing it.
			console.error(`notification delivery failed: ${String(error)}`);
		});
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
): MailHubConnection {
	const hub = createMailHubConnection(
		new HubConnectionBuilder()
			// Relative: the page is served by the backend, so the handshake carries the httpOnly
			// launch cookie and no token appears in the URL (§9).
			.withUrl("/hub")
			.withAutomaticReconnect()
			// At Information SignalR logs negotiated URLs.
			.configureLogging(LogLevel.Warning)
			.build(),
	);

	hub.subscribe("syncProgress", (progress: SyncProgressDto) => {
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
	hub.subscribe("accountStatusChanged", (account: { id: string }) => {
		void queryClient.invalidateQueries({ queryKey: ["accounts"] });
		void queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(account.id),
		});
	});

	hub.subscribe("accountRemoved", (accountId: string) => {
		removeAccountCaches(queryClient, accountId);
		void queryClient.invalidateQueries({ queryKey: ["accounts"] });
		if (windowStore.getState("selectedAccountId") !== accountId) return;
		windowStore.setState("selectedAccountId", null);
		windowStore.setState("selectedMailboxId", null);
		windowStore.setState("selectedMessageId", null);
		windowStore.setState("selectedMessageSubject", "");
		windowStore.setState("selectedMessageSenderAddress", "");
	});

	// These account-scoped receiver events invalidate only the changed account, preserving
	// unrelated windows' active calendar and sender-identity caches.
	hub.subscribe("calendarCollectionChanged", (accountId: string) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.calendars(accountId),
		});
	});
	hub.subscribe("sendIdentitiesChanged", (accountId: string) => {
		void queryClient.invalidateQueries({
			queryKey: ["send-identities", accountId],
		});
	});

	hub.subscribe("mailboxUpdated", (mailbox: { accountId: string }) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(mailbox.accountId),
		});
	});

	hub.subscribe("mailboxTreeChanged", (accountId: string) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(accountId),
		});
	});
	hub.subscribe("contactsChanged", (accountId: string) => {
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
	const invalidateMessageCaches = () => {
		void Promise.all([
			queryClient.invalidateQueries({ queryKey: ["messages"] }),
			queryClient.invalidateQueries({ queryKey: ["search"] }),
			queryClient.invalidateQueries({ queryKey: ["pending"] }),
		]);
	};
	hub.subscribe("messageReceived", invalidateMessageCaches);
	hub.subscribe("messageUpdated", (message: MessageSummaryDto) => {
		updateMessageSummaryCaches(queryClient, message);
		invalidateMessageCaches();
	});
	hub.subscribe("messageDeleted", invalidateMessageCaches);

	hub.subscribe("messageMutationQueued", (queued: MutationQueuedDto) => {
		void queryClient.invalidateQueries({
			queryKey: queryKeys.pending(queued.accountId),
		});
		if (queued.sourceMailboxId)
			hideOptimisticMessages(queryClient, queued.sourceMailboxId, [
				{
					messageId: queued.messageId,
					claimId: queued.mutationItemId,
				},
			]);
	});

	hub.subscribe("messageMutationSettled", (settlement: MutationSettledDto) => {
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
	hub.subscribe("messageDeleted", (messageId: string) => {
		void queryClient.invalidateQueries({ queryKey: ["body", messageId] });
	});

	// A draft created, saved, deleted, pushed to the server, or materialised locally by sync
	// (§7) — the event only carries draftId, not accountId, so this invalidates by prefix
	// like the message events above rather than trying to scope it.
	hub.subscribe("draftUpdated", () => {
		void queryClient.invalidateQueries({ queryKey: ["drafts"] });
	});

	// One calm offline state rather than per-mailbox error noise (§7, §15) — written
	// directly rather than invalidated, the same cache-only pattern SyncProgress uses above,
	// since there is nothing to refetch: the event already carries the new value.
	hub.subscribe("connectivityChanged", (online: boolean) => {
		queryClient.setQueryData(queryKeys.connectivity(), online);
	});

	// Theme/close-behaviour/mailto-prompt and the remote-content rules (§13 Epics 8, 5)
	// changed in some window — every window converges per Epic 10's "all actions reflected
	// live across all open windows." Panel layout/window bounds are deliberately excluded
	// upstream (a read-once-at-open default, not something every window syncs to).
	hub.subscribe("shellSettingsChanged", () => {
		void queryClient.invalidateQueries({ queryKey: ["shell-settings"] });
	});

	hub.subscribe("remoteContentRulesChanged", () => {
		void queryClient.invalidateQueries({
			queryKey: ["remote-content-rules"],
		});
	});

	// A change the user asked for that will not happen. Shown, not logged: the optimistic
	// state has already been reverted, so without this the flag springs back with no
	// explanation and the user is left believing the app is simply unreliable.
	hub.subscribe("messageSyncFailed", (failure: MutationFailureDto) => {
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
		const opensReauthenticate =
			presentation.action === "reauthenticate" ||
			presentation.action === "trust-certificate";
		if (!presentation.silent) {
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
		}

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
	const invalidateCalendarEventCaches = () => {
		void queryClient.invalidateQueries({ queryKey: ["calendar-events"] });
		void queryClient.invalidateQueries({
			queryKey: ["calendar-event-detail"],
		});
		void queryClient.invalidateQueries({ queryKey: ["invite"] });
	};
	hub.subscribe("calendarEventUpdated", invalidateCalendarEventCaches);
	hub.subscribe("calendarConflictDetected", invalidateCalendarEventCaches);

	// Dispatch is the shell's job, not this window's (§13 Epic 9): relay straight to the
	// preload bridge, and confirm delivery only once the shell has actually shown it — a
	// crash between these two steps redelivers the same notification rather than losing it.
	hub.subscribe("notificationReady", (notification: NotificationDto) => {
		dispatchNativeNotification(hub, notification, window.notifications);
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
				: hub.getTerminalMutationIds(mutationIds).catch((error: unknown) => {
						console.error(
							`optimistic mutation reconciliation failed: ${String(error)}`,
						);
						return [];
					});
		const accountId = windowStore.getState("selectedAccountId");
		const mailboxId = windowStore.getState("selectedMailboxId");
		if (accountId && mailboxId) {
			void hub.setActiveMailbox(accountId, mailboxId);
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
