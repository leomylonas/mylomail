import { useEffect, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { MailboxTree } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { MessageList } from "@mylomail/renderer/Components/MessageList/MessageList";
import { SearchBox } from "@mylomail/renderer/Components/SearchBox/SearchBox";
import {
	Compose,
	type OpenDraft,
} from "@mylomail/renderer/Components/Compose/Compose";
import { DraftList } from "@mylomail/renderer/Components/DraftList/DraftList";
import {
	AccountSettings,
	type AccountSettingsValues,
} from "@mylomail/renderer/Components/AccountSettings/AccountSettings";
import { AddAccount } from "@mylomail/renderer/Components/AddAccount/AddAccount";
import { Calendar } from "@mylomail/renderer/Components/Calendar/Calendar";
import { Button } from "@carbon/react";
import { ReadingPane } from "@mylomail/renderer/Components/ReadingPane/ReadingPane";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
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
		"reading" | "compose" | "settings" | "drafts" | "add-account" | "calendar"
	>("reading");
	const [openDraft, setOpenDraft] = useState<OpenDraft | undefined>();
	const store = useWindowStore();
	const { store: notifications } = useWindowNotifications();
	const selectedAccountId = useStoreValue(store, "selectedAccountId");
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");
	const selectedMessageId = useStoreValue(store, "selectedMessageId");
	const selectedMessageSubject = useStoreValue(store, "selectedMessageSubject");
	const sidebarWidth = useStoreValue(store, "sidebarWidth");

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
		<div
			className={styles.shell}
			style={{ ["--mylomail-sidebar-width" as string]: `${sidebarWidth}px` }}
		>
			<header className={styles.header}>
				<h1 className={styles.title}>MyloMail</h1>
				<span className={styles.status}>{describe(status, accounts.data)}</span>
				<Button
					size="sm"
					disabled={!selectedAccountId}
					onClick={() => {
						setOpenDraft(undefined);
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
			</header>

			<div className={styles.panels}>
				{effectivePane === "calendar" && hub && accounts.data?.length ? (
					<div className={styles.calendarPanel}>
						<Calendar hub={hub} accounts={accounts.data} />
					</div>
				) : null}
				{effectivePane !== "calendar" && hub && selectedAccountId ? (
					<MailboxTree hub={hub} accountId={selectedAccountId} />
				) : effectivePane !== "calendar" ? (
					<div />
				) : null}
				{effectivePane !== "calendar" ? (
					<div className={styles.reading}>
						<SearchBox query={query} onChange={setQuery} />
						{hub && selectedAccountId && selectedMailboxId ? (
							<MessageList
								hub={hub}
								accountId={selectedAccountId}
								mailboxId={selectedMailboxId}
								query={query}
								onSelect={(message) => {
									store.setState("selectedMessageId", message.id);
									store.setState("selectedMessageSubject", message.subject);
								}}
							/>
						) : (
							<p style={{ padding: "1rem" }}>Select a mailbox.</p>
						)}
					</div>
				) : null}
				{hub && selectedAccountId && effectivePane === "compose" ? (
					<Compose
						key={openDraft?.id ?? "new"}
						hub={hub}
						accountId={selectedAccountId}
						draft={openDraft}
						onClose={() => setPane("reading")}
					/>
				) : null}
				{hub && selectedAccountId && effectivePane === "drafts" ? (
					<div className={styles.draftPanel}>
						<DraftList
							hub={hub}
							accountId={selectedAccountId}
							onOpen={(draft) => {
								setOpenDraft(draft);
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
				{hub && selectedMessageId && effectivePane === "reading" ? (
					<ReadingPane
						hub={hub}
						messageId={selectedMessageId}
						subject={selectedMessageSubject}
					/>
				) : null}
				{effectivePane === "add-account" ? (
					<AddAccount
						onAdded={() => {
							void queryClient.invalidateQueries({ queryKey: ["accounts"] });
							setPane("reading");
						}}
					/>
				) : null}
			</div>
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
	};
}

function describe(status: string, accounts: Account[] | undefined): string {
	if (status === "failed") return "Disconnected from the backend.";
	if (status === "connecting") return "Connecting…";
	if (!accounts?.length) return "No accounts yet.";
	return accounts[0].emailAddress;
}
