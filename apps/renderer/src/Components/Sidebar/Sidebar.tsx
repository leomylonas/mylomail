import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { MailboxTree } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { accountDragType } from "@mylomail/renderer/Lib/DragTypes";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/Sidebar/Sidebar.module.css";

interface SidebarAccount {
	id: string;
	displayName: string;
	color: string;
	sidebarCollapsed?: boolean;
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

	// Without this a failed drag-to-reorder or collapse toggle just silently reverted on the
	// next accounts refetch, indistinguishable from the app having ignored the action —
	// the same reasoning MailboxTree's own mutations already follow.
	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, {
			kind: "error",
			title,
			detail: error instanceof Error ? error.message : String(error),
		});

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
							<span className={styles.name}>{account.displayName}</span>
						</button>
						{isCollapsed ? null : (
							<MailboxTree hub={hub} accountId={account.id} />
						)}
					</section>
				);
			})}
		</nav>
	);
}
