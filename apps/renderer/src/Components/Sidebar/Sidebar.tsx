import { useEffect, useRef, useState } from "react";
import { WarningAltFilled } from "@carbon/icons-react";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useMutation, useQueries, useQueryClient } from "@tanstack/react-query";
import {
	MailboxIcon,
	MailboxTree,
	mailboxesQuery,
} from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import {
	inboxUnreadCount,
	movePinnedBefore,
	pinnedMailboxes,
} from "@mylomail/renderer/Components/MailboxTree/MailboxOrdering";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import {
	accountDragType,
	pinnedMailboxDragType,
} from "@mylomail/renderer/Lib/DragTypes";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import {
	AuthState,
	type ProviderType,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/Sidebar/Sidebar.module.css";

interface SidebarAccount {
	id: string;
	displayName: string;
	color: string;
	sidebarCollapsed?: boolean;
	authState?: AuthState;
	providerType?: ProviderType;
}

/**
 * Every account's mailboxes, visible at once — no switching that hides other accounts (§13
 * Epic 2). Each account is a top-level, collapsible tree section with its own `MailboxTree`
 * nested beneath it, the same shape Outlook's folder pane uses. Account order is a drag on the
 * section header, persisted the same way `MailboxTree`'s own folder reorder is. Collapse state
 * is server-persisted too (§13 Epic 2: "expand/collapse state persists... across restarts"),
 * not component state — the account list already round-trips through the accounts query, so
 * that's the one source of truth rather than a second, locally-forgotten copy.
 */
export function Sidebar({
	hub,
	accounts,
	onMailboxSelected,
}: {
	hub: MailHubConnection;
	accounts: SidebarAccount[];
	/** Called only for the user's own click on a folder, never when selection is restored. */
	onMailboxSelected?: (mailboxId: string) => void;
}) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const [dropTarget, setDropTarget] = useState<string | null>(null);
	const windowStore = useWindowStore();
	const selectedMailboxId = useStoreValue(windowStore, "selectedMailboxId");
	const navRef = useRef<HTMLElement>(null);
	// The folder a pinned-row click asked the tree to reveal. Cleared once it has been scrolled to,
	// which can be several renders later: expanding its ancestors is a server round trip.
	const [revealId, setRevealId] = useState<string | null>(null);
	const [pinnedDropTarget, setPinnedDropTarget] = useState<string | null>(null);
	const [pinnedMenu, setPinnedMenu] = useState<{
		x: number;
		y: number;
		mailboxId: string;
	} | null>(null);
	// Accounts whose hidden folders are currently shown in place. Per window, not persisted: it is
	// a way of looking, not a setting.
	const [accountsShowingHidden, setAccountsShowingHidden] = useState<
		ReadonlySet<string>
	>(new Set());
	const [menu, setMenu] = useState<{
		x: number;
		y: number;
		account: SidebarAccount;
	} | null>(null);

	// Without this a failed drag-to-reorder or collapse toggle just silently reverted on the
	// next accounts refetch, indistinguishable from the app having ignored the action —
	// the same reasoning MailboxTree's own mutations already follow.
	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, notificationForError(error, title));

	const reorder = useMutation({
		mutationFn: (orderedAccountIds: string[]) =>
			hub.reorderAccounts(orderedAccountIds),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["accounts"] }),
		onError: reportFailure("The accounts could not be reordered"),
	});

	const toggleCollapsed = useMutation({
		mutationFn: (input: { accountId: string; collapsed: boolean }) =>
			hub.setAccountSidebarCollapsed(input.accountId, input.collapsed),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["accounts"] }),
		onError: reportFailure("The sidebar setting could not be saved"),
	});

	const unpin = useMutation({
		mutationFn: (input: { mailboxId: string; pinned: boolean }) =>
			hub.setMailboxPinned(input.mailboxId, input.pinned),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["mailboxes"] }),
		onError: reportFailure("The folder could not be unpinned"),
	});

	const reorderPinned = useMutation({
		mutationFn: (orderedMailboxIds: string[]) =>
			hub.reorderPinnedMailboxes(orderedMailboxIds),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["mailboxes"] }),
		onError: reportFailure("The pinned folders could not be reordered"),
	});

	// Same queries each MailboxTree runs, so this adds no fetches; it is what lets a
	// collapsed account show its inbox count and the pinned section span accounts.
	const mailboxQueries = useQueries({
		queries: accounts.map((account) => mailboxesQuery(hub, account.id)),
	});
	const mailboxesByAccount = new Map(
		accounts.map((account, index) => [
			account.id,
			mailboxQueries[index]?.data ?? [],
		]),
	);
	const pinned = pinnedMailboxes(accounts, mailboxesByAccount);
	// Opens whatever hides a folder — its collapsed account and collapsed ancestors — then lets
	// the effect below scroll to it once the tree has rendered it.
	const revealMailbox = (accountId: string, mailbox: { id: string }) => {
		const account = accounts.find((candidate) => candidate.id === accountId);
		const all = mailboxesByAccount.get(accountId) ?? [];
		if (account?.sidebarCollapsed)
			toggleCollapsed.mutate({ accountId, collapsed: false });
		let parentId = all.find((m) => m.id === mailbox.id)?.parentId ?? null;
		while (parentId) {
			const parent = all.find((m) => m.id === parentId);
			if (!parent) break;
			if (parent.isCollapsed)
				void hub
					.setMailboxCollapsed(parent.id, false)
					.then(() =>
						queryClient.invalidateQueries({ queryKey: ["mailboxes"] }),
					);
			parentId = parent.parentId;
		}
		setRevealId(mailbox.id);
	};
	const treeVersion = `${mailboxQueries.map((query) => query.dataUpdatedAt).join()}|${accounts
		.map((account) => account.sidebarCollapsed)
		.join()}`;
	useEffect(() => {
		if (!revealId) return;
		const element = navRef.current?.querySelector(
			`[data-mailbox-id="${revealId}"]`,
		);
		if (!element) return;
		// Scrolls the sidebar only: scrollIntoView also scrolls every ancestor, which moved the
		// whole app up inside the window.
		const nav = navRef.current!;
		const offset =
			element.getBoundingClientRect().top - nav.getBoundingClientRect().top;
		nav.scrollTop += offset - nav.clientHeight / 2;
		setRevealId(null);
		// Re-runs as the tree's data and the accounts' expand state arrive.
	}, [revealId, treeVersion]);
	const pinnedIds = pinned.map((entry) => entry.mailbox.id);
	const movePinned = (draggedId: string, targetId: string) => {
		const ordered = movePinnedBefore(pinnedIds, draggedId, targetId);
		if (ordered) reorderPinned.mutate(ordered);
	};

	return (
		<nav
			ref={navRef}
			className={styles.sidebar}
			aria-label="Accounts and mailboxes"
		>
			{pinned.length > 0 ? (
				<section
					className={`${styles.section} ${styles.pinnedSection}`}
					aria-label="Pinned folders"
				>
					<h2 className={styles.pinnedHeading}>Pinned</h2>
					<ul className={styles.pinnedList}>
						{pinned.map((entry) => {
							const isSelected = entry.mailbox.id === selectedMailboxId;
							return (
								<li
									key={entry.mailbox.id}
									className={
										pinnedDropTarget === entry.mailbox.id
											? styles.dropTarget
											: undefined
									}
									onDragOver={(event) => {
										if (
											event.dataTransfer.types.includes(pinnedMailboxDragType)
										) {
											event.preventDefault();
											setPinnedDropTarget(entry.mailbox.id);
										}
									}}
									onDragLeave={() =>
										setPinnedDropTarget((current) =>
											current === entry.mailbox.id ? null : current,
										)
									}
									onDrop={(event) => {
										event.preventDefault();
										setPinnedDropTarget(null);
										const draggedId = event.dataTransfer.getData(
											pinnedMailboxDragType,
										);
										if (draggedId) movePinned(draggedId, entry.mailbox.id);
									}}
								>
									<button
										type="button"
										draggable
										onDragStart={(event) => {
											event.dataTransfer.setData(
												pinnedMailboxDragType,
												entry.mailbox.id,
											);
											event.dataTransfer.effectAllowed = "move";
										}}
										className={`${styles.pinnedItem} ${isSelected ? styles.selected : ""}`}
										aria-current={isSelected}
										onClick={() => {
											windowStore.setState(
												"selectedAccountId",
												entry.accountId,
											);
											windowStore.setState(
												"selectedMailboxId",
												entry.mailbox.id,
											);
											onMailboxSelected?.(entry.mailbox.id);
											revealMailbox(entry.accountId, entry.mailbox);
										}}
										onContextMenu={(event) => {
											event.preventDefault();
											setPinnedMenu({
												x: event.clientX,
												y: event.clientY,
												mailboxId: entry.mailbox.id,
											});
										}}
									>
										<span className={styles.pinnedIcon} aria-hidden="true">
											<MailboxIcon
												specialUse={
													entry.mailbox.specialUseOverride ??
													entry.mailbox.specialUse
												}
											/>
										</span>
										<span className={styles.name}>
											{entry.label}
											{entry.accountQualifier ? (
												<span className={styles.qualifier}>
													{" "}
													({entry.accountQualifier})
												</span>
											) : null}
										</span>
										{entry.mailbox.unreadCount ? (
											<span className={styles.unreadCount}>
												{entry.mailbox.unreadCount}
											</span>
										) : null}
									</button>
								</li>
							);
						})}
					</ul>
				</section>
			) : null}
			{pinnedMenu ? (
				<MessageContextMenu
					open
					x={pinnedMenu.x}
					y={pinnedMenu.y}
					onClose={() => setPinnedMenu(null)}
					actions={[
						{
							label: "Move up",
							unavailable:
								pinnedIds.indexOf(pinnedMenu.mailboxId) <= 0
									? "Already first."
									: undefined,
							run: () =>
								movePinned(
									pinnedMenu.mailboxId,
									pinnedIds[pinnedIds.indexOf(pinnedMenu.mailboxId) - 1],
								),
						},
						{
							label: "Move down",
							unavailable:
								pinnedIds.indexOf(pinnedMenu.mailboxId) === pinnedIds.length - 1
									? "Already last."
									: undefined,
							run: () =>
								movePinned(
									pinnedIds[pinnedIds.indexOf(pinnedMenu.mailboxId) + 1],
									pinnedMenu.mailboxId,
								),
						},
						{ label: "-", run: () => undefined },
						{
							label: "Unpin from top",
							run: () =>
								unpin.mutate({
									mailboxId: pinnedMenu.mailboxId,
									pinned: false,
								}),
						},
					]}
				/>
			) : null}
			{accounts.map((account) => {
				const isCollapsed = account.sidebarCollapsed ?? false;
				const authWarning =
					account.authState !== undefined &&
					account.authState !== AuthState.Connected
						? authStateWarning(account.authState)
						: null;
				const inboxUnread = inboxUnreadCount(
					mailboxesByAccount.get(account.id) ?? [],
				);
				return (
					<section key={account.id} className={styles.section}>
						<button
							type="button"
							draggable
							className={`${styles.header} ${dropTarget === account.id ? styles.dropTarget : ""}`}
							aria-expanded={!isCollapsed}
							onClick={() =>
								toggleCollapsed.mutate({
									accountId: account.id,
									collapsed: !isCollapsed,
								})
							}
							onContextMenu={(event) => {
								event.preventDefault();
								setMenu({ x: event.clientX, y: event.clientY, account });
							}}
							onDragStart={(event) => {
								event.dataTransfer.setData(accountDragType, account.id);
								event.dataTransfer.effectAllowed = "move";
							}}
							onDragOver={(event) => {
								if (event.dataTransfer.types.includes(accountDragType)) {
									event.preventDefault();
									setDropTarget(account.id);
								}
							}}
							onDragLeave={() =>
								setDropTarget((current) =>
									current === account.id ? null : current,
								)
							}
							onDrop={(event) => {
								event.preventDefault();
								setDropTarget(null);
								const draggedId = event.dataTransfer.getData(accountDragType);
								if (!draggedId || draggedId === account.id) return;

								const ids = accounts.map((a) => a.id);
								const from = ids.indexOf(draggedId);
								const to = ids.indexOf(account.id);
								if (from === -1 || to === -1) return;
								ids.splice(to, 0, ...ids.splice(from, 1));
								reorder.mutate(ids);
							}}
						>
							<span className={styles.chevron} aria-hidden>
								{isCollapsed ? "▸" : "▾"}
							</span>
							<span
								className={styles.swatch}
								style={{
									backgroundColor: account.color || "var(--cds-icon-secondary)",
								}}
								aria-hidden
							/>
							<span className={styles.name} title={account.displayName}>
								{account.displayName}
							</span>
							{authWarning ? (
								<span
									className={styles.warning}
									aria-label={authWarning}
									title={authWarning}
								>
									<WarningAltFilled size={16} />
								</span>
							) : null}
							{isCollapsed && inboxUnread > 0 ? (
								<span
									className={styles.unreadCount}
									aria-label={`${inboxUnread} unread in Inbox`}
								>
									{inboxUnread}
								</span>
							) : null}
						</button>
						{isCollapsed ? null : (
							<MailboxTree
								hub={hub}
								accountId={account.id}
								providerType={account.providerType}
								showHidden={accountsShowingHidden.has(account.id)}
								onMailboxSelected={onMailboxSelected}
							/>
						)}
					</section>
				);
			})}
			{menu ? (
				<MessageContextMenu
					open
					x={menu.x}
					y={menu.y}
					onClose={() => setMenu(null)}
					actions={[
						...accountMoveActions(accounts, menu.account, (orderedIds) =>
							reorder.mutate(orderedIds),
						),
						{ label: "-", run: () => undefined },
						(() => {
							const accountId = menu.account.id;
							const showing = accountsShowingHidden.has(accountId);
							const hiddenCount = (
								queryClient.getQueryData<{ isHidden: boolean }[]>(
									queryKeys.mailboxes(accountId),
								) ?? []
							).filter((mailbox) => mailbox.isHidden).length;
							return {
								label: showing ? "Hide hidden folders" : "Show hidden folders",
								run: () =>
									setAccountsShowingHidden((current) => {
										const next = new Set(current);
										if (showing) next.delete(accountId);
										else next.add(accountId);
										return next;
									}),
								unavailable:
									!showing && hiddenCount === 0
										? "This account has no hidden folders."
										: undefined,
							};
						})(),
					]}
				/>
			) : null}
		</nav>
	);
}

/**
 * Reordering an account among its siblings previously had no keyboard/context-menu path at all
 * — only reachable by dragging its section header onto another (§13 Epic 2), the identical gap
 * pass 201 closed for moving a message and pass 202 closed for a mailbox's own reorder/reparent.
 * A pure function, like `mailboxMoveActions`, so the menu contents are testable without
 * rendering the sidebar.
 */
export function accountMoveActions(
	accounts: SidebarAccount[],
	account: SidebarAccount,
	reorder: (orderedAccountIds: string[]) => void,
) {
	const ids = accounts.map((a) => a.id);
	const index = ids.indexOf(account.id);

	const swapWith = (otherIndex: number) => {
		const reordered = [...ids];
		[reordered[index], reordered[otherIndex]] = [
			reordered[otherIndex],
			reordered[index],
		];
		reorder(reordered);
	};

	return [
		{
			label: "Move up",
			run: () => swapWith(index - 1),
			unavailable: index <= 0 ? "Already first" : undefined,
		},
		{
			label: "Move down",
			run: () => swapWith(index + 1),
			unavailable:
				index === -1 || index >= ids.length - 1 ? "Already last" : undefined,
		},
	];
}

/**
 * §13 Epic 1: "View per-account connection status" — with every account's mailboxes shown at
 * once (no account switcher), a non-`Connected` account previously looked identical to a
 * working one in this list; nothing here surfaced it until the user happened to select that
 * specific account and saw `AppShell`'s own banner. A pure function so this mapping is testable
 * without mounting the component.
 */
export function authStateWarning(authState: AuthState): string {
	switch (authState) {
		case AuthState.NeedsReauth:
			return "This account needs to be reauthenticated.";
		case AuthState.CredentialStoreUnavailable:
			return "This account's credentials are unavailable — unlock your keychain.";
		case AuthState.Error:
			return "This account has a connection problem.";
		default:
			return "This account is not connected.";
	}
}
