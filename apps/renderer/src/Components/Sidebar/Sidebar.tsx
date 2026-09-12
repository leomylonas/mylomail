import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { MailboxTree } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { accountDragType } from "@mylomail/renderer/Lib/DragTypes";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { AuthState } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/Sidebar/Sidebar.module.css";

interface SidebarAccount {
	id: string;
	displayName: string;
	color: string;
	sidebarCollapsed?: boolean;
	authState?: AuthState;
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
}: {
	hub: HubConnection;
	accounts: SidebarAccount[];
}) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const [dropTarget, setDropTarget] = useState<string | null>(null);
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
			hub.invoke("ReorderAccounts", orderedAccountIds),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["accounts"] }),
		onError: reportFailure("The accounts could not be reordered"),
	});

	const toggleCollapsed = useMutation({
		mutationFn: (input: { accountId: string; collapsed: boolean }) =>
			hub.invoke(
				"SetAccountSidebarCollapsed",
				input.accountId,
				input.collapsed,
			),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["accounts"] }),
		onError: reportFailure("The sidebar setting could not be saved"),
	});

	return (
		<nav className={styles.sidebar} aria-label="Accounts and mailboxes">
			{accounts.map((account) => {
				const isCollapsed = account.sidebarCollapsed ?? false;
				const authWarning =
					account.authState !== undefined &&
					account.authState !== AuthState.Connected
						? authStateWarning(account.authState)
						: null;
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
								<span role="img" aria-label={authWarning} title={authWarning}>
									⚠️
								</span>
							) : null}
						</button>
						{isCollapsed ? null : (
							<MailboxTree hub={hub} accountId={account.id} />
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
					actions={accountMoveActions(accounts, menu.account, (orderedIds) =>
						reorder.mutate(orderedIds),
					)}
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
