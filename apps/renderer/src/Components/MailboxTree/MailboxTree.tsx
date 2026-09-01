import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { Modal, SkeletonText } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import { FolderNameModal } from "@mylomail/renderer/Components/MailboxTree/FolderNameModal/FolderNameModal";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import {
	mailboxDragType,
	messageDragType,
} from "@mylomail/renderer/Lib/DragTypes";
import styles from "@mylomail/renderer/Components/MailboxTree/MailboxTree.module.css";

interface Mailbox {
	id: string;
	parentId: string | null;
	name: string;
	providerTotalCount: number | null;
	providerUnreadCount: number | null;
	localCount: number;
}

interface Capabilities {
	deletingMailboxDeletesMessages: boolean;
}

type Dialog =
	| { kind: "create"; parent: Mailbox | null }
	| { kind: "rename"; mailbox: Mailbox }
	| { kind: "delete"; mailbox: Mailbox };

export function MailboxTree({
	hub,
	accountId,
}: {
	hub: HubConnection;
	accountId: string;
}) {
	const store = useWindowStore();
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");
	const [menu, setMenu] = useState<{
		x: number;
		y: number;
		mailbox: Mailbox;
	} | null>(null);
	const [dialog, setDialog] = useState<Dialog | null>(null);
	const [dropTarget, setDropTarget] = useState<string | null>(null);

	const close = () => setDialog(null);
	const refresh = () => {
		close();
		return queryClient.invalidateQueries({
			queryKey: queryKeys.mailboxes(accountId),
		});
	};

	// Folder operations are the one part of the sidebar that can fail visibly to the user:
	// the provider rejects a duplicate name, a namespace it will not accept, or a delete of a
	// special folder. Without this the dialog simply stayed open and said nothing, which is
	// indistinguishable from the app having ignored the click.
	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, {
			kind: "error",
			title,
			detail: error instanceof Error ? error.message : String(error),
		});

	const create = useMutation({
		mutationFn: ({
			name,
			parentId,
		}: {
			name: string;
			parentId: string | null;
		}) => hub.invoke("CreateMailbox", accountId, name, parentId),
		onSuccess: refresh,
		onError: reportFailure("The folder could not be created"),
	});

	const rename = useMutation({
		mutationFn: ({ id, name }: { id: string; name: string }) =>
			hub.invoke("RenameMailbox", id, name),
		onSuccess: refresh,
		onError: reportFailure("The folder could not be renamed"),
	});

	// Drag-a-message-onto-a-folder and folder drag-reorder (§13 Epic 2). Native HTML5 DnD: no
	// library pulls its own weight for two drop targets, and Carbon's buttons already forward
	// arbitrary DOM props like `draggable`/`onDragStart`/`onDrop`.
	const moveMessages = useMutation({
		mutationFn: (input: { messageIds: string[]; targetMailboxId: string }) =>
			hub.invoke(
				"MoveMessages",
				accountId,
				input.messageIds,
				input.targetMailboxId,
			),
		onSuccess: () => {
			void queryClient.invalidateQueries({ queryKey: ["messages"] });
			void queryClient.invalidateQueries({
				queryKey: queryKeys.pending(accountId),
			});
		},
		onError: reportFailure("The message could not be moved"),
	});

	const moveMailbox = useMutation({
		mutationFn: (input: { mailboxId: string; newParentId: string | null }) =>
			hub.invoke("MoveMailbox", input.mailboxId, input.newParentId),
		onSuccess: refresh,
		onError: reportFailure("The folder could not be moved"),
	});

	const reorderMailboxes = useMutation({
		mutationFn: (input: {
			parentId: string | null;
			orderedMailboxIds: string[];
		}) =>
			hub.invoke(
				"ReorderMailboxes",
				accountId,
				input.parentId,
				input.orderedMailboxIds,
			),
		onSuccess: refresh,
		onError: reportFailure("The folders could not be reordered"),
	});

	const remove = useMutation({
		mutationFn: (mailbox: Mailbox) =>
			hub.invoke<boolean>("DeleteMailbox", mailbox.id),
		onSuccess: async (messagesWentToo, mailbox) => {
			// Reported after the fact as well as warned about before it, because the provider
			// is the one that decides and the answer is not the same on every account.
			notify(notifications, {
				kind: "success",
				title: `Deleted "${mailbox.name}"`,
				detail: messagesWentToo
					? "Its messages were deleted with it."
					: "Its messages are still in All Mail.",
			});

			if (mailbox.id === selectedMailboxId) {
				store.setState("selectedMailboxId", null);
			}

			await refresh();
		},
		onError: reportFailure("The folder could not be deleted"),
	});

	const mailboxes = useQuery({
		queryKey: queryKeys.mailboxes(accountId),
		queryFn: () => hub.invoke<Mailbox[]>("GetMailboxes", accountId),
	});

	// Fetched alongside the tree rather than when the confirmation opens: a dialog that has to
	// wait for a round trip before it can say what deleting does would either flash the wrong
	// wording or block on the network at the moment the user is deciding.
	const capabilities = useQuery({
		queryKey: queryKeys.accountCapabilities(accountId),
		queryFn: () =>
			hub.invoke<Capabilities>("GetAccountCapabilities", accountId),
	});

	if (mailboxes.isPending) return <SkeletonText paragraph lineCount={5} />;
	if (mailboxes.isError) return <p>Could not load mailboxes.</p>;

	const children = (parentId: string | null) =>
		mailboxes.data.filter((mailbox) => (mailbox.parentId ?? null) === parentId);

	const dropOnMailbox = (event: React.DragEvent, target: Mailbox) => {
		event.preventDefault();
		setDropTarget(null);

		const messageIds = event.dataTransfer.getData(messageDragType);
		if (messageIds) {
			moveMessages.mutate({
				messageIds: messageIds.split(","),
				targetMailboxId: target.id,
			});
			return;
		}

		const draggedMailboxId = event.dataTransfer.getData(mailboxDragType);
		if (!draggedMailboxId || draggedMailboxId === target.id) return;

		const dragged = mailboxes.data.find((m) => m.id === draggedMailboxId);
		if (!dragged || isDescendantOf(mailboxes.data, target, draggedMailboxId))
			return;

		if ((dragged.parentId ?? null) === (target.parentId ?? null)) {
			// Same parent: a reorder, dropped mailbox lands immediately before the target.
			const siblingIds = children(target.parentId ?? null)
				.map((m) => m.id)
				.filter((id) => id !== draggedMailboxId);
			siblingIds.splice(siblingIds.indexOf(target.id), 0, draggedMailboxId);
			reorderMailboxes.mutate({
				parentId: target.parentId ?? null,
				orderedMailboxIds: siblingIds,
			});
		} else {
			moveMailbox.mutate({
				mailboxId: draggedMailboxId,
				newParentId: target.id,
			});
		}
	};

	const renderLevel = (parentId: string | null, depth: number) => (
		<ul>
			{children(parentId).map((mailbox) => (
				<li key={mailbox.id}>
					<button
						type="button"
						draggable
						className={`${styles.item} ${mailbox.id === selectedMailboxId ? styles.selected : ""} ${dropTarget === mailbox.id ? styles.dropTarget : ""}`}
						style={{
							paddingLeft: `calc(var(--cds-spacing-03) * ${depth + 1})`,
						}}
						aria-current={mailbox.id === selectedMailboxId}
						onClick={() => store.setState("selectedMailboxId", mailbox.id)}
						onContextMenu={(event) => {
							event.preventDefault();
							setMenu({ x: event.clientX, y: event.clientY, mailbox });
						}}
						onDragStart={(event) => {
							event.dataTransfer.setData(mailboxDragType, mailbox.id);
							event.dataTransfer.effectAllowed = "move";
						}}
						onDragOver={(event) => {
							event.preventDefault();
							event.dataTransfer.dropEffect = "move";
						}}
						onDragEnter={() => setDropTarget(mailbox.id)}
						onDragLeave={() =>
							setDropTarget((current) =>
								current === mailbox.id ? null : current,
							)
						}
						onDrop={(event) => dropOnMailbox(event, mailbox)}
					>
						<span>{mailbox.name}</span>
						<span className={styles.count}>{describeCount(mailbox)}</span>
					</button>
					{renderLevel(mailbox.id, depth + 1)}
				</li>
			))}
		</ul>
	);

	return (
		<>
			<nav className={styles.tree} aria-label="Mailboxes">
				{renderLevel(null, 0)}
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
							run: () => setDialog({ kind: "create", parent: menu.mailbox }),
						},
						{
							label: "New folder",
							run: () => setDialog({ kind: "create", parent: null }),
						},
						{ label: "-", run: () => undefined },
						{
							label: "Rename",
							run: () => setDialog({ kind: "rename", mailbox: menu.mailbox }),
						},
						{
							label: "Delete",
							run: () => setDialog({ kind: "delete", mailbox: menu.mailbox }),
							danger: true,
						},
					]}
				/>
			) : null}
			{dialog?.kind === "create" ? (
				<FolderNameModal
					heading={
						dialog.parent ? `New folder in ${dialog.parent.name}` : "New folder"
					}
					label="Folder name"
					primaryLabel="Create"
					onSubmit={(name) =>
						create.mutate({ name, parentId: dialog.parent?.id ?? null })
					}
					onClose={close}
				/>
			) : null}
			{dialog?.kind === "rename" ? (
				<FolderNameModal
					key={dialog.mailbox.id}
					heading={`Rename ${dialog.mailbox.name}`}
					label="Folder name"
					initialName={dialog.mailbox.name}
					primaryLabel="Rename"
					onSubmit={(name) => rename.mutate({ id: dialog.mailbox.id, name })}
					onClose={close}
				/>
			) : null}
			{dialog?.kind === "delete" ? (
				<Modal
					open
					danger
					modalHeading={`Delete "${dialog.mailbox.name}"?`}
					primaryButtonText="Delete"
					secondaryButtonText="Cancel"
					onRequestSubmit={() => remove.mutate(dialog.mailbox)}
					onRequestClose={close}
					onSecondarySubmit={close}
				>
					<p>{describeDeletion(capabilities.data)}</p>
				</Modal>
			) : null}
		</>
	);
}

/**
 * Whether `candidate` is `ancestorId` itself or sits anywhere beneath it.
 *
 * Guards the reparent drop: a folder dropped onto its own descendant would give that
 * descendant's `ParentId` chain a cycle, which is what {@link renderLevel}'s recursion walks —
 * an infinite tree with no way back out.
 */
function isDescendantOf(
	mailboxes: Mailbox[],
	candidate: Mailbox,
	ancestorId: string,
): boolean {
	let current: Mailbox | undefined = candidate;
	while (current) {
		if (current.id === ancestorId) return true;
		current = mailboxes.find((m) => m.id === current!.parentId);
	}
	return false;
}

/**
 * What deleting this folder will do, according to the provider.
 *
 * Deleting an IMAP or Graph folder destroys the mail inside it; deleting a Gmail label does
 * not. Until the answer is known the wording commits to neither — a confirmation that
 * guesses is worse than one that waits, because the user acts on it (§2).
 */
function describeDeletion(capabilities: Capabilities | undefined): string {
	if (capabilities === undefined) {
		return "Checking what this will do to the messages in it…";
	}

	return capabilities.deletingMailboxDeletesMessages
		? "The messages in this folder will be deleted with it. This cannot be undone."
		: "This removes the label. Its messages stay in All Mail.";
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
