import { useEffect, useRef, useState } from "react";
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
import { ActionableNotification, Button } from "@carbon/react";
import { ReadingPane } from "@mylomail/renderer/Components/ReadingPane/ReadingPane";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useShellLayout } from "@mylomail/renderer/Shell/Layout/UseShellLayout";
import {
	AuthState,
	CertificateTrustMode,
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
	authState?: AuthState;
	lastAuthError?: string | null;
	sidebarCollapsed?: boolean;
	attachmentSizeLimitOverride?: number | null;
	certificateTrustMode?: CertificateTrustMode;
}

/**
 * The window's panel layout.
 *
 * Two panels for now, but the split is real: the sidebar and the list read from the same
 * per-window store rather than passing selection down through props, which is what lets a
 * second window hold a different selection without either knowing the other exists.
 */
export function AppShell() {
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
	>("reading");
	const [openDraft, setOpenDraft] = useState<OpenDraft | undefined>();
	// A reply/reply-all/forward's prefill, before any draft exists to hold it (§13). Cleared
	// whenever an existing draft is opened instead, the same way `openDraft` is cleared for
	// "New message" — the two are mutually exclusive seeds for the same compose pane.
	const [composeSeed, setComposeSeed] = useState<ComposeSeed | undefined>();
	const [reauthenticating, setReauthenticating] = useState(false);
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
	const layout = useShellLayout();
	const sidebarRef = useRef<PanelImperativeHandle>(null);
	const detailRef = useRef<PanelImperativeHandle>(null);

	const queryClient = useQueryClient();
	const accounts = useQuery({
		queryKey: ["accounts"],
		queryFn: async (): Promise<Account[]> => {
			// Same-origin, so the launch cookie authenticates this without a token.
			const response = await fetch("/accounts");
			if (!response.ok)
				throw new Error(`accounts responded ${response.status}`);
			return (await response.json()) as Account[];
		},
	});

	// Selecting the only account is not a decision worth making the user repeat.
	useEffect(() => {
		if (!selectedAccountId && accounts.data?.length)
			store.setState("selectedAccountId", accounts.data[0].id);
	}, [accounts.data, selectedAccountId, store]);

	// The front door: with nothing set up yet, the form is what the user should see, not an
	// empty reading pane with no way to get past it. Derived rather than synced via an effect,
	// so there is no first-render flash of the reading pane before the accounts query settles.
	const effectivePane = accounts.data?.length === 0 ? "add-account" : pane;
	const selectedAccount = accounts.data?.find(
		(a) => a.id === selectedAccountId,
	);
	const needsAttention =
		selectedAccount &&
		selectedAccount.authState !== undefined &&
		selectedAccount.authState !== AuthState.Connected;

	// Clicking a notification opens the app and navigates to the message (§13 Epic 9).
	// Subscribing to the shell's IPC channel is exactly what an effect is for; the store
	// update happens inside the callback, in response to that external event, not during
	// render. A null messageId means the notification was recorded from a still-staged,
	// not-yet-replayed change-stream page — fetched on demand here rather than the
	// navigation simply failing.
	useEffect(
		() =>
			window.notifications?.onClicked(({ notificationId, messageId }) => {
				if (messageId) {
					store.setState("selectedMessageId", messageId);
					setPane("reading");
					return;
				}

				void hub
					?.invoke<string | null>("ResolveStagedMessage", notificationId)
					.then((resolved) => {
						if (resolved) {
							store.setState("selectedMessageId", resolved);
							setPane("reading");
						} else {
							notify(notifications, {
								kind: "info",
								title: "Still syncing",
								detail: "This message hasn't finished downloading yet.",
							});
						}
					});
			}),
		[hub, store, notifications],
	);

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
					onClick={() => {
						setOpenDraft(undefined);
						setComposeSeed(undefined);
						setPane("compose");
					}}
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
					onActionButtonClick={() => setReauthenticating(true)}
				/>
			) : null}

			{effectivePane === "calendar" && hub && accounts.data?.length ? (
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
												void window.windows?.open(
													`compose=${draftId}&account=${selectedAccountId}`,
												);
												setPane("reading");
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
										setOpenDraft(draft);
										setComposeSeed(undefined);
										setPane("compose");
									}}
								/>
							</div>
						) : null}
						{hub && selectedAccountId && effectivePane === "settings" ? (
							<AccountSettings
								hub={hub}
								initial={toSettings(accounts.data, selectedAccountId)}
								onClose={() => setPane("reading")}
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
								onOpenInNewWindow={
									window.windows
										? () =>
												void window.windows?.open(
													`message=${selectedMessageId}&subject=${encodeURIComponent(
														selectedMessageSubject,
													)}`,
												)
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

			{reauthenticating && hub && selectedAccountId ? (
				<ReauthenticateAccount
					hub={hub}
					accountId={selectedAccountId}
					onReauthenticated={() => setReauthenticating(false)}
					onClose={() => setReauthenticating(false)}
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
		certificateTrustMode:
			account?.certificateTrustMode ?? CertificateTrustMode.Default,
		attachmentSizeLimitOverride: account?.attachmentSizeLimitOverride ?? null,
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
