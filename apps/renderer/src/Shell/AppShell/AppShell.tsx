import { useCallback, useEffect, useRef, useState } from "react";
import {
	Group,
	Panel,
	Separator,
	type PanelImperativeHandle,
} from "react-resizable-panels";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Sidebar } from "@mylomail/renderer/Components/Sidebar/Sidebar";
import { MessageList } from "@mylomail/renderer/Components/MessageList/MessageList";
import { SearchBox } from "@mylomail/renderer/Components/SearchBox/SearchBox";
import {
	Compose,
	type OpenDraft,
} from "@mylomail/renderer/Components/Compose/Compose";
import type { ComposeSeed } from "@mylomail/renderer/Components/Compose/ComposeReplyForward";
import { buildMailtoSeed } from "@mylomail/renderer/Components/Compose/ComposeMailto";
import type { MailtoComposeRequest } from "@mylomail/electron-shell/Mailto";
import { DraftList } from "@mylomail/renderer/Components/DraftList/DraftList";
import {
	AccountSettings,
	type AccountSettingsValues,
} from "@mylomail/renderer/Components/AccountSettings/AccountSettings";
import { ShellSettings } from "@mylomail/renderer/Components/ShellSettings/ShellSettings";
import { AddAccount } from "@mylomail/renderer/Components/AddAccount/AddAccount";
import { ReauthenticateAccount } from "@mylomail/renderer/Components/ReauthenticateAccount/ReauthenticateAccount";
import { Calendar } from "@mylomail/renderer/Components/Calendar/Calendar";
import { ConnectivityBanner } from "@mylomail/renderer/Components/ConnectivityBanner/ConnectivityBanner";
import { Contacts } from "@mylomail/renderer/Components/Contacts/Contacts";
import { ActionableNotification, Button } from "@carbon/react";
import { ReadingPane } from "@mylomail/renderer/Components/ReadingPane/ReadingPane";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import {
	fetchApi,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useShellLayout } from "@mylomail/renderer/Shell/Layout/UseShellLayout";
import { useShortcuts } from "@mylomail/renderer/Shell/Registries/Shortcuts/UseShortcuts";
import {
	applyNotificationNavigation,
	waitForNotificationNavigation,
} from "@mylomail/renderer/Shell/NotificationNavigation";
import {
	AuthState,
	CertificateTrustMode,
	InitialSyncMode,
	ProviderType,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Shell/AppShell/AppShell.module.css";

interface Account {
	id: string;
	displayName: string;
	emailAddress: string;
	color: string;
	pollIntervalSeconds?: number;
	pollingEnabled?: boolean;
	undoSendDelaySeconds?: number;
	notificationsEnabled?: boolean;
	initialSyncMode?: InitialSyncMode;
	initialSyncBoundValue?: number | null;
	authState?: AuthState;
	lastAuthError?: string | null;
	sidebarCollapsed?: boolean;
	attachmentSizeLimitOverride?: number | null;
	certificateTrustMode?: CertificateTrustMode;
	providerType?: ProviderType;
	appendToSentOnSend?: boolean | null;
	isThrottled?: boolean;
}

export interface NotificationClick {
	notificationId: string;
	accountId: string;
}

/**
 * The window's panel layout.
 *
 * Two panels for now, but the split is real: the sidebar and the list read from the same
 * per-window store rather than passing selection down through props, which is what lets a
 * second window hold a different selection without either knowing the other exists.
 */
export function AppShell({
	initialNotification,
	initialMailto,
}: {
	initialNotification?: NotificationClick;
	initialMailto?: MailtoComposeRequest;
}) {
	const { hub, status } = useHub();
	const [query, setQuery] = useState("");
	const [pane, setPane] = useState<
		| "reading"
		| "compose"
		| "settings"
		| "app-settings"
		| "drafts"
		| "add-account"
		| "calendar"
		| "contacts"
	>(initialMailto ? "compose" : "reading");
	const [openDraft, setOpenDraft] = useState<OpenDraft | undefined>();
	// A reply/reply-all/forward's prefill, before any draft exists to hold it (§13). Cleared
	// whenever an existing draft is opened instead, the same way `openDraft` is cleared for
	// "New message" — the two are mutually exclusive seeds for the same compose pane.
	const [composeSeed, setComposeSeed] = useState<ComposeSeed | undefined>(() =>
		initialMailto ? buildMailtoSeed(initialMailto) : undefined,
	);
	const [printRequest, setPrintRequest] = useState<{
		messageId: string;
		requestId: string;
	} | null>(null);
	const handlePrintHandled = useCallback((requestId: string) => {
		setPrintRequest((current) =>
			current?.requestId === requestId ? null : current,
		);
	}, []);
	// Which account's ReauthenticateAccount dialog is open, if any — not always
	// selectedAccountId: a MessageSyncFailed event (§15) can request this for a different
	// account than whichever one this window currently has selected.
	const [reauthenticatingAccountId, setReauthenticatingAccountId] = useState<
		string | null
	>(null);
	const store = useWindowStore();
	const { store: notifications } = useWindowNotifications();
	const selectedAccountId = useStoreValue(store, "selectedAccountId");
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");
	const selectedMessageId = useStoreValue(store, "selectedMessageId");
	const selectedMessageSubject = useStoreValue(store, "selectedMessageSubject");
	const selectedMessageSenderAddress = useStoreValue(
		store,
		"selectedMessageSenderAddress",
	);
	const layout = useShellLayout((error) =>
		notify(
			notifications,
			notificationForError(error, "The panel layout could not be saved"),
		),
	);
	const sidebarRef = useRef<PanelImperativeHandle>(null);
	const detailRef = useRef<PanelImperativeHandle>(null);
	const initialNotificationHandled = useRef(false);

	const queryClient = useQueryClient();
	const accounts = useQuery({
		queryKey: ["accounts"],
		queryFn: async (): Promise<Account[]> => {
			// Same-origin, so the launch cookie authenticates this without a token.
			const response = await fetchApi("/accounts");
			return (await response.json()) as Account[];
		},
	});

	// Selecting the only account is not a decision worth making the user repeat.
	useEffect(() => {
		if (!selectedAccountId && accounts.data?.length)
			store.setState("selectedAccountId", accounts.data[0].id);
	}, [accounts.data, selectedAccountId, store]);

	// IMAP IDLE is deliberately limited to Inbox plus the mailbox this window is viewing.
	// This is a non-durable wakeup hint only; the backend's normal poll loop remains the
	// authoritative cursor-owning sync path.
	useEffect(() => {
		if (hub && selectedAccountId && selectedMailboxId) {
			void hub.invoke("SetActiveMailbox", selectedAccountId, selectedMailboxId);
		}
	}, [hub, selectedAccountId, selectedMailboxId]);

	// A MessageSyncFailed event (§15) asked this window to open ReauthenticateAccount for a
	// specific account — not necessarily whichever one is currently selected. An external-event
	// subscription, the same shape as the notification-click listener below, not a React-state
	// sync: the write happens in response to that external event firing, not during render.
	useEffect(
		() =>
			queryClient.getQueryCache().subscribe((event) => {
				if (
					event.type !== "updated" ||
					event.query.queryKey[0] !== "reauth-requested-account"
				) {
					return;
				}
				const accountId = event.query.state.data as string | null;
				if (accountId) {
					setReauthenticatingAccountId(accountId);
					queryClient.setQueryData(queryKeys.reauthRequestedAccountId(), null);
				}
			}),
		[queryClient],
	);

	// The front door: with nothing set up yet, the form is what the user should see, not an
	// empty reading pane with no way to get past it. Derived rather than synced via an effect,
	// so there is no first-render flash of the reading pane before the accounts query settles.
	const effectivePane = accounts.data?.length === 0 ? "add-account" : pane;
	const selectedAccount = accounts.data?.find(
		(a) => a.id === selectedAccountId,
	);
	// CredentialStoreUnavailable gets its own banner below: reauthenticating cannot fix a
	// locked OS keychain, so it must not offer the same "Reauthenticate" action as the other
	// non-Connected states.
	const credentialStoreUnavailable =
		selectedAccount?.authState === AuthState.CredentialStoreUnavailable;
	const needsAttention =
		selectedAccount &&
		selectedAccount.authState !== undefined &&
		selectedAccount.authState !== AuthState.Connected &&
		!credentialStoreUnavailable;

	// Reports this window's own currently-editing draft to the shell, so a different window
	// can check `focusDraftIfOpen` before opening the same one — the two exist together to stop
	// a draft being edited independently in two windows at once, which would otherwise autosave
	// as a silent last-write-wins race (§13, §15). Only an existing draft (never a brand-new,
	// unsaved compose seed, which has no id to collide on) is worth reporting.
	const openDraftId =
		effectivePane === "compose" ? (openDraft?.id ?? null) : null;
	useEffect(() => {
		void window.windows?.reportDraftState(openDraftId);
	}, [openDraftId]);

	// The shell elects one main window for a native-notification click. Resolve the route from
	// durable local identity at click time: the OS payload may predate staged Gmail replay or a
	// provider move, and a message id alone cannot select its account/mailbox or supply the
	// subject/sender context the reading pane needs.
	useEffect(() => {
		if (!hub) return;

		let activeController: AbortController | undefined;
		const openNotification = (clicked: NotificationClick) => {
			activeController?.abort();
			const controller = new AbortController();
			activeController = controller;

			// Move to the known account immediately and clear the old account's selection while
			// a staged message waits for canonical replay.
			store.setState("selectedAccountId", clicked.accountId);
			store.setState("selectedMailboxId", null);
			store.setState("selectedMessageId", null);
			store.setState("selectedMessageSubject", "");
			store.setState("selectedMessageSenderAddress", "");
			setQuery("");
			setPane("reading");

			void waitForNotificationNavigation(
				(notificationId) =>
					hub.invoke("ResolveNotificationNavigation", notificationId),
				clicked.notificationId,
				controller.signal,
				() =>
					notify(notifications, {
						kind: "info",
						title: "Still syncing",
						detail:
							"This message is still being added. MyloMail will open it as soon as it is ready.",
					}),
			)
				.then((navigation) => {
					if (activeController === controller) activeController = undefined;
					if (controller.signal.aborted) return;
					const isInitial =
						initialNotification?.notificationId === clicked.notificationId;
					if (
						navigation === null ||
						!applyNotificationNavigation(store, navigation)
					) {
						notify(notifications, {
							kind: "info",
							title: "Message unavailable",
							detail:
								"This notification's message is no longer available locally.",
						});
						if (isInitial) initialNotificationHandled.current = true;
						return;
					}

					setOpenDraft(undefined);
					setComposeSeed(undefined);
					setPane("reading");
					void queryClient.invalidateQueries({ queryKey: ["accounts"] });
					void queryClient.invalidateQueries({
						queryKey: queryKeys.mailboxes(navigation.accountId),
					});
					void queryClient.invalidateQueries({
						queryKey: queryKeys.messages(navigation.mailboxId),
					});
					void queryClient.invalidateQueries({
						queryKey: ["body", navigation.messageId],
					});
					if (isInitial) initialNotificationHandled.current = true;
				})
				.catch((error: unknown) => {
					if (activeController === controller) activeController = undefined;
					if (controller.signal.aborted) return;
					notify(
						notifications,
						notificationForError(error, "Couldn't open that message"),
					);
				});
		};

		const unsubscribe = window.notifications?.onClicked(openNotification);
		window.notifications?.setNavigationReady(true);
		if (initialNotification && !initialNotificationHandled.current) {
			openNotification(initialNotification);
		}

		return () => {
			window.notifications?.setNavigationReady(false);
			unsubscribe?.();
			activeController?.abort();
		};
	}, [hub, initialNotification, notifications, queryClient, store]);

	// "c" for compose is the one standard Outlook/Gmail shortcut this shell never wired
	// (§13) — every other shell-level action already has its own affordance, and the
	// message-list actions (u/i/s/Delete/r/a/f) live in their own registry scoped to that
	// component. Guarded identically to the button's own `disabled` condition below.
	const openComposeShortcut = () => {
		if (!selectedAccountId) return;
		setOpenDraft(undefined);
		setComposeSeed(undefined);
		setPane("compose");
	};
	useShortcuts([
		{ key: "c", description: "New message", run: openComposeShortcut },
	]);

	return (
		<div className={styles.shell}>
			<header className={styles.header}>
				<h1 className={styles.title}>MyloMail</h1>
				<span className={styles.status}>
					{describe(status, accounts.data, accounts.isError)}
				</span>
				<Button
					size="sm"
					disabled={!selectedAccountId}
					onClick={openComposeShortcut}
				>
					New message
				</Button>
				<Button
					size="sm"
					kind="ghost"
					disabled={!selectedAccountId}
					onClick={() => setPane("drafts")}
				>
					Drafts
				</Button>
				<Button
					size="sm"
					kind="ghost"
					disabled={!selectedAccountId}
					onClick={() => setPane("settings")}
				>
					Account settings
				</Button>
				<Button size="sm" kind="ghost" onClick={() => setPane("app-settings")}>
					Settings
				</Button>
				<Button size="sm" kind="ghost" onClick={() => setPane("add-account")}>
					Add account
				</Button>
				<Button
					size="sm"
					kind="ghost"
					disabled={!accounts.data?.length}
					onClick={() => setPane("calendar")}
				>
					Calendar
				</Button>
				{window.windows ? (
					<Button
						size="sm"
						kind="ghost"
						onClick={() => void window.windows?.open()}
					>
						New window
					</Button>
				) : null}
				{selectedAccountId && hub ? (
					<Button size="sm" kind="ghost" onClick={() => setPane("contacts")}>
						Contacts
					</Button>
				) : null}
				{effectivePane !== "calendar" ? (
					<>
						<Button
							size="sm"
							kind="ghost"
							onClick={() =>
								sidebarRef.current?.isCollapsed()
									? sidebarRef.current.expand()
									: sidebarRef.current?.collapse()
							}
						>
							Toggle sidebar
						</Button>
						<Button
							size="sm"
							kind="ghost"
							onClick={() =>
								detailRef.current?.isCollapsed()
									? detailRef.current.expand()
									: detailRef.current?.collapse()
							}
						>
							Toggle reading pane
						</Button>
					</>
				) : null}
			</header>

			<div className={styles.screenOnly}>
				{hub ? <ConnectivityBanner hub={hub} /> : null}

				{needsAttention ? (
					<ActionableNotification
						kind="warning"
						title="This account needs attention"
						subtitle={
							selectedAccount!.lastAuthError ??
							"MyloMail could not sign in to this account."
						}
						lowContrast
						hideCloseButton
						inline
						actionButtonLabel="Reauthenticate"
						onActionButtonClick={() =>
							setReauthenticatingAccountId(selectedAccountId ?? null)
						}
					/>
				) : null}

				{credentialStoreUnavailable ? (
					<ActionableNotification
						kind="warning"
						title="Unlock your keychain"
						subtitle={
							selectedAccount!.lastAuthError ??
							"MyloMail could not reach your OS credential store. This account's stored password may be fine — nothing to re-enter here, it just needs your keychain unlocked."
						}
						lowContrast
						hideCloseButton
						inline
					/>
				) : null}
			</div>

			{effectivePane === "contacts" && hub && selectedAccountId ? (
				<div className={styles.calendarPanel}>
					<Contacts
						key={selectedAccountId}
						hub={hub}
						accountId={selectedAccountId}
						providerType={selectedAccount?.providerType ?? ProviderType.Imap}
					/>
				</div>
			) : effectivePane === "calendar" && hub && accounts.data?.length ? (
				<div className={styles.calendarPanel}>
					<Calendar hub={hub} accounts={accounts.data} />
				</div>
			) : (
				<Group
					className={styles.panels}
					defaultLayout={{
						sidebar: layout.initial.sidebar,
						list: layout.initial.list,
						detail: layout.initial.detail,
					}}
					onLayoutChanged={(sizes, meta) => {
						// Only a direct drag writes back the shared default — recomputes from
						// constraints or the initial mount are not a user's stated preference.
						if (!meta.isUserInteraction) return;
						layout.onResize({
							sidebar: sizes.sidebar,
							list: sizes.list,
							detail: sizes.detail,
						});
					}}
				>
					<Panel
						id="sidebar"
						minSize="15"
						collapsible
						collapsedSize="0"
						panelRef={sidebarRef}
					>
						{hub && accounts.data?.length ? (
							<Sidebar hub={hub} accounts={accounts.data} />
						) : (
							<div />
						)}
					</Panel>
					<Separator className={styles.handle} />
					<Panel id="list" minSize="20">
						<div className={styles.reading}>
							<SearchBox query={query} onChange={setQuery} />
							{hub && selectedAccountId && selectedMailboxId ? (
								<MessageList
									hub={hub}
									accountId={selectedAccountId}
									ownAddress={selectedAccount?.emailAddress ?? ""}
									mailboxId={selectedMailboxId}
									query={query}
									onSelect={(message) => {
										store.setState("selectedMessageId", message.id);
										store.setState("selectedMessageSubject", message.subject);
										store.setState(
											"selectedMessageSenderAddress",
											message.from,
										);
									}}
									onPrint={(message) => {
										store.setState("selectedMessageId", message.id);
										store.setState("selectedMessageSubject", message.subject);
										store.setState(
											"selectedMessageSenderAddress",
											message.from,
										);
										setPane("reading");
										setPrintRequest({
											messageId: message.id,
											requestId: crypto.randomUUID(),
										});
									}}
									onCompose={(seed) => {
										setOpenDraft(undefined);
										setComposeSeed(seed);
										setPane("compose");
									}}
								/>
							) : (
								<p style={{ padding: "1rem" }}>Select a mailbox.</p>
							)}
						</div>
					</Panel>
					<Separator className={styles.handle} />
					<Panel
						id="detail"
						minSize="20"
						collapsible
						collapsedSize="0"
						panelRef={detailRef}
					>
						{hub && selectedAccountId && effectivePane === "compose" ? (
							<Compose
								key={openDraft?.id ?? composeSeed?.key ?? "new"}
								hub={hub}
								accountId={selectedAccountId}
								draft={openDraft}
								seed={composeSeed}
								onClose={() => setPane("reading")}
								onDetach={
									window.windows
										? (draftId) => {
												void (async () => {
													const focusedExisting =
														await window.windows?.focusDraftIfOpen(draftId);
													if (focusedExisting) {
														setPane("reading");
														return;
													}

													try {
														await window.windows?.open(
															`compose=${draftId}&account=${selectedAccountId}`,
														);
														setPane("reading");
													} catch {
														notify(notifications, {
															kind: "error",
															title: "Could not open in a new window",
															detail: "The draft is still open here instead.",
														});
													}
												})();
											}
										: undefined
								}
							/>
						) : null}
						{hub && selectedAccountId && effectivePane === "drafts" ? (
							<div className={styles.draftPanel}>
								<DraftList
									hub={hub}
									accountId={selectedAccountId}
									onOpen={(draft) => {
										void (async () => {
											const focusedExisting =
												await window.windows?.focusDraftIfOpen(draft.id);
											if (focusedExisting) {
												notify(notifications, {
													kind: "info",
													title: "Already open",
													detail:
														"This draft is being edited in another window.",
												});
												return;
											}

											setOpenDraft(draft);
											setComposeSeed(undefined);
											setPane("compose");
										})();
									}}
								/>
							</div>
						) : null}
						{hub && selectedAccountId && effectivePane === "settings" ? (
							<AccountSettings
								hub={hub}
								initial={toSettings(accounts.data, selectedAccountId)}
								isThrottled={
									accounts.data?.find(
										(account) => account.id === selectedAccountId,
									)?.isThrottled
								}
								onClose={() => setPane("reading")}
								onRemoved={() => {
									// Cleared, not left pointing at a now-gone account: the
									// existing "select the first account" effect only fires
									// when this is falsy, and a removed account's id would
									// otherwise linger as a selection nothing can resolve.
									store.setState("selectedAccountId", null);
									void queryClient.invalidateQueries({
										queryKey: ["accounts"],
									});
									setPane("reading");
								}}
							/>
						) : null}
						{effectivePane === "app-settings" ? (
							<ShellSettings onClose={() => setPane("reading")} />
						) : null}
						{hub && selectedMessageId && effectivePane === "reading" ? (
							<ReadingPane
								hub={hub}
								messageId={selectedMessageId}
								subject={selectedMessageSubject}
								senderAddress={selectedMessageSenderAddress}
								printRequestId={
									printRequest?.messageId === selectedMessageId
										? printRequest.requestId
										: undefined
								}
								onPrintHandled={handlePrintHandled}
								onOpenInNewWindow={
									window.windows
										? () =>
												void window.windows
													?.open(
														`message=${selectedMessageId}&subject=${encodeURIComponent(
															selectedMessageSubject,
														)}${
															selectedMessageSenderAddress
																? `&sender=${encodeURIComponent(selectedMessageSenderAddress)}`
																: ""
														}`,
													)
													.catch(() => {
														notify(notifications, {
															kind: "error",
															title: "Could not open in a new window",
															detail: "The message is still open here instead.",
														});
													})
										: undefined
								}
							/>
						) : null}
						{effectivePane === "add-account" ? (
							<AddAccount
								onAdded={() => {
									void queryClient.invalidateQueries({
										queryKey: ["accounts"],
									});
									setPane("reading");
								}}
							/>
						) : null}
					</Panel>
				</Group>
			)}

			{reauthenticatingAccountId && hub ? (
				<ReauthenticateAccount
					hub={hub}
					accountId={reauthenticatingAccountId}
					onReauthenticated={() => setReauthenticatingAccountId(null)}
					onClose={() => setReauthenticatingAccountId(null)}
				/>
			) : null}
		</div>
	);
}

/** The settings form's starting values, from the account list the shell already holds. */
function toSettings(
	accounts: Account[] | undefined,
	accountId: string,
): AccountSettingsValues {
	const account = accounts?.find((candidate) => candidate.id === accountId);

	return {
		id: accountId,
		displayName: account?.displayName ?? "",
		color: account?.color ?? "",
		pollIntervalSeconds: account?.pollIntervalSeconds ?? 60,
		pollingEnabled: account?.pollingEnabled ?? true,
		undoSendDelaySeconds: account?.undoSendDelaySeconds ?? 0,
		notificationsEnabled: account?.notificationsEnabled ?? true,
		initialSyncMode: account?.initialSyncMode ?? InitialSyncMode.Full,
		initialSyncBoundValue: account?.initialSyncBoundValue ?? null,
		certificateTrustMode:
			account?.certificateTrustMode ?? CertificateTrustMode.Default,
		attachmentSizeLimitOverride: account?.attachmentSizeLimitOverride ?? null,
		providerType: account?.providerType ?? ProviderType.Imap,
		appendToSentOnSend: account?.appendToSentOnSend ?? null,
	};
}

function describe(
	status: string,
	accounts: Account[] | undefined,
	accountsErrored: boolean,
): string {
	if (status === "failed") return "Disconnected from the backend.";
	if (status === "connecting") return "Connecting…";
	if (accountsErrored) return "Could not load accounts.";
	if (!accounts?.length) return "No accounts yet.";
	return accounts[0].emailAddress;
}
