import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { accountDragType } from "@mylomail/renderer/Lib/DragTypes";
import styles from "@mylomail/renderer/Components/AccountSwitcher/AccountSwitcher.module.css";

interface SwitcherAccount {
	id: string;
	displayName: string;
	color: string;
}

/**
 * Switches between accounts and reorders them (§13 Epic 1). Native HTML5 drag-and-drop, the
 * same convention `MailboxTree`'s sidebar reorder uses — no drag library pulls its own weight
 * for one reorderable list.
 */
export function AccountSwitcher({
	hub,
	accounts,
	selectedAccountId,
	onSelect,
}: {
	hub: HubConnection;
	accounts: SwitcherAccount[];
	selectedAccountId: string | null;
	onSelect: (accountId: string) => void;
}) {
	const queryClient = useQueryClient();
	const [dropTarget, setDropTarget] = useState<string | null>(null);

	const reorder = useMutation({
		mutationFn: (orderedAccountIds: string[]) =>
			hub.invoke("ReorderAccounts", orderedAccountIds),
		onSuccess: () =>
			void queryClient.invalidateQueries({ queryKey: ["accounts"] }),
	});

	if (accounts.length < 2) return null;

	return (
		<ul className={styles.switcher} aria-label="Accounts">
			{accounts.map((account) => (
				<li key={account.id}>
					<button
						type="button"
						draggable
						aria-pressed={account.id === selectedAccountId}
						className={`${styles.chip} ${account.id === selectedAccountId ? styles.selected : ""} ${dropTarget === account.id ? styles.dropTarget : ""}`}
						onClick={() => onSelect(account.id)}
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
						onDragLeave={() => setDropTarget(null)}
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
						<span
							className={styles.swatch}
							style={{
								backgroundColor: account.color || "var(--cds-icon-secondary)",
							}}
							aria-hidden
						/>
						{account.displayName}
					</button>
				</li>
			))}
		</ul>
	);
}
