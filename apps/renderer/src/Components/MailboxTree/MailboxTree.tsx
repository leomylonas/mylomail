import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import styles from "@mylomail/renderer/Components/MailboxTree/MailboxTree.module.css";

interface Mailbox {
	id: string;
	name: string;
	providerTotalCount: number | null;
	providerUnreadCount: number | null;
	localCount: number;
}

export function MailboxTree({
	hub,
	accountId,
}: {
	hub: HubConnection;
	accountId: string;
}) {
	const store = useWindowStore();
	const queryClient = useQueryClient();
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");
	const [menu, setMenu] = useState<{
		x: number;
		y: number;
		mailbox: Mailbox;
	} | null>(null);

	const refresh = () =>
		queryClient.invalidateQueries({ queryKey: queryKeys.mailboxes(accountId) });

	const create = useMutation({
		mutationFn: (parentId: string | null) => {
			const name = window.prompt("Name for the new folder");
			return name
				? hub.invoke("CreateMailbox", accountId, name, parentId)
				: Promise.resolve();
		},
		onSuccess: refresh,
	});

	const rename = useMutation({
		mutationFn: (mailbox: Mailbox) => {
			const name = window.prompt("Rename folder to", mailbox.name);
			return name
				? hub.invoke("RenameMailbox", mailbox.id, name)
				: Promise.resolve();
		},
		onSuccess: refresh,
	});

	const remove = useMutation({
		// The provider decides whether the messages go too, and the answer differs: deleting
		// an IMAP folder destroys its mail, deleting a Gmail label does not. The confirmation
		// has to say which, so it is asked before the call and the result reported after (§2).
		mutationFn: (mailbox: Mailbox) =>
			window.confirm(
				`Delete "${mailbox.name}"? On this account that deletes the messages in it.`,
			)
				? hub.invoke<boolean>("DeleteMailbox", mailbox.id)
				: Promise.resolve(false),
		onSuccess: refresh,
	});

	const mailboxes = useQuery({
		queryKey: queryKeys.mailboxes(accountId),
		queryFn: () => hub.invoke<Mailbox[]>("GetMailboxes", accountId),
	});

	if (mailboxes.isPending) return <SkeletonText paragraph lineCount={5} />;
	if (mailboxes.isError) return <p>Could not load mailboxes.</p>;

	return (
		<>
			<nav className={styles.tree} aria-label="Mailboxes">
				<ul>
					{mailboxes.data.map((mailbox) => (
						<li key={mailbox.id}>
							<button
								type="button"
								className={`${styles.item} ${mailbox.id === selectedMailboxId ? styles.selected : ""}`}
								aria-current={mailbox.id === selectedMailboxId}
								onClick={() => store.setState("selectedMailboxId", mailbox.id)}
								onContextMenu={(event) => {
									event.preventDefault();
									setMenu({ x: event.clientX, y: event.clientY, mailbox });
								}}
							>
								<span>{mailbox.name}</span>
								<span className={styles.count}>{describeCount(mailbox)}</span>
							</button>
						</li>
					))}
				</ul>
			</nav>
			{menu ? (
				<MessageContextMenu
					open
					x={menu.x}
					y={menu.y}
					onClose={() => setMenu(null)}
					actions={[
						{
							label: "New subfolder",
							run: () => create.mutate(menu.mailbox.id),
						},
						{ label: "New folder", run: () => create.mutate(null) },
						{ label: "-", run: () => undefined },
						{ label: "Rename", run: () => rename.mutate(menu.mailbox) },
						{
							label: "Delete",
							run: () => remove.mutate(menu.mailbox),
							danger: true,
						},
					]}
				/>
			) : null}
		</>
	);
}

/**
 * The provider's count where there is one, and the local count otherwise.
 *
 * Under a bounded sync the local count is simply wrong as a mailbox total — "last 3 months"
 * of a large inbox holds a fraction of it — so where the server tells us, that is what the
 * sidebar shows. Where it cannot, the local count is shown as what it is (§1).
 */
function describeCount(mailbox: Mailbox): string {
	if (mailbox.providerUnreadCount !== null && mailbox.providerUnreadCount > 0) {
		return `${mailbox.providerUnreadCount}`;
	}

	return mailbox.providerTotalCount === null
		? `${mailbox.localCount} held`
		: "";
}
