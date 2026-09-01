import { useState } from "react";
import {
	useMutation,
	useQuery,
	useQueryClient,
	type QueryClient,
} from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { MessageContextMenu } from "@mylomail/renderer/Shell/Registries/ContextMenus/MessageContextMenu/MessageContextMenu";
import type { MenuAction } from "@mylomail/renderer/Shell/Registries/ContextMenus/ContextMenus";
import { useShortcuts } from "@mylomail/renderer/Shell/Registries/Shortcuts/UseShortcuts";
import { messageDragType } from "@mylomail/renderer/Lib/DragTypes";
import styles from "@mylomail/renderer/Components/MessageList/MessageList.module.css";

interface MessageSummary {
	id: string;
	subject: string;
	snippet: string;
	from: { name: string | null; email: string }[];
	receivedAt: string;
	isRead: boolean;
	isFlagged: boolean;
}

interface PendingChange {
	messageId: string;
	field: number;
	desiredValue: boolean;
}

const isReadField = 0;

export function MessageList({
	hub,
	accountId,
	mailboxId,
	query,
	onSelect,
	onPrint,
}: {
	hub: HubConnection;
	accountId: string;
	mailboxId: string;
	query: string;
	onSelect: (message: { id: string; subject: string; from: string }) => void;
	/**
	 * Opens the message in the reading pane, the same as {@link onSelect}, but the caller
	 * additionally switches to it: printing has to go through the reading pane's own sandboxed
	 * `MessageHtml` rendering rather than a second, ad-hoc render path for remote-authored
	 * content (§13).
	 */
	onPrint: (message: { id: string; subject: string; from: string }) => void;
}) {
	const queryClient = useQueryClient();
	const searching = query.trim().length > 0;
	const [menu, setMenu] = useState<{
		x: number;
		y: number;
		message: MessageSummary;
	} | null>(null);

	// One list, two sources. Searching scopes to the selected mailbox, because a search from
	// inside a folder that silently returned results from everywhere would be a different
	// question than the one the user asked.
	const messages = useQuery({
		queryKey: searching
			? queryKeys.search(accountId, query, mailboxId)
			: queryKeys.messages(mailboxId),
		queryFn: () =>
			searching
				? hub.invoke<MessageSummary[]>("Search", accountId, query, mailboxId)
				: hub.invoke<MessageSummary[]>("GetMessages", mailboxId, 100),
	});

	// What the user has asked for and the server has not yet confirmed. Merged over
	// server-known state so a flag they just toggled does not flicker back while its mutation
	// is in flight (§6).
	const pending = useQuery({
		queryKey: queryKeys.pending(accountId),
		queryFn: () =>
			hub.invoke<PendingChange[]>("GetPendingSyncState", accountId),
	});

	const setFlags = useMutation({
		mutationFn: ({
			message,
			isRead,
			isFlagged,
		}: {
			message: MessageSummary;
			isRead: boolean | null;
			isFlagged: boolean | null;
		}) => hub.invoke("SetFlags", accountId, [message.id], isRead, isFlagged),
		onSettled: () =>
			queryClient.invalidateQueries({ queryKey: queryKeys.pending(accountId) }),
	});

	const trash = useMutation({
		mutationFn: (message: MessageSummary) =>
			hub.invoke("MoveToTrash", accountId, [message.id]),
		onSettled: () => queryClient.invalidateQueries({ queryKey: ["messages"] }),
	});

	// Bound to the message under the cursor, following Gmail and Outlook conventions. Every
	// action goes through the mutation queue, so a shortcut and its menu entry cannot diverge
	// in what they actually do (§13).
	const selected = menu?.message;
	useShortcuts([
		{
			key: "u",
			description: "Mark unread",
			run: () =>
				selected &&
				setFlags.mutate({ message: selected, isRead: false, isFlagged: null }),
		},
		{
			key: "i",
			description: "Mark read",
			run: () =>
				selected &&
				setFlags.mutate({ message: selected, isRead: true, isFlagged: null }),
		},
		{
			key: "s",
			description: "Flag",
			run: () =>
				selected &&
				setFlags.mutate({
					message: selected,
					isRead: null,
					isFlagged: !selected.isFlagged,
				}),
		},
	]);

	if (messages.isPending) return <SkeletonText paragraph lineCount={6} />;
	if (messages.isError)
		return <p className={styles.empty}>Could not load messages.</p>;
	if (messages.data.length === 0)
		return (
			<p className={styles.empty}>
				{searching ? "No messages match that search." : "Nothing here yet."}
			</p>
		);

	return (
		<>
			<ul className={styles.list}>
				{messages.data.map((message) => {
					const read = isRead(message, pending.data);
					return (
						<li key={message.id}>
							<button
								type="button"
								className={`${styles.row} ${read ? "" : styles.unread}`}
								draggable
								onDragStart={(event) => {
									event.dataTransfer.setData(messageDragType, message.id);
									event.dataTransfer.effectAllowed = "move";
								}}
								onContextMenu={(event) => {
									event.preventDefault();
									setMenu({ x: event.clientX, y: event.clientY, message });
								}}
								onClick={() => {
									onSelect({ ...message, from: senderAddress(message) });
									// Opening a message marks it read, as every mail client does.
									// Already-read messages enqueue nothing: a redundant mutation
									// would still be a real provider call.
									if (!read)
										setFlags.mutate({ message, isRead: true, isFlagged: null });
								}}
							>
								<span>
									{message.subject || "(no subject)"}
									<br />
									<span className={styles.sender}>
										{describeSender(message)}
									</span>
								</span>
								<span className={styles.sender}>
									{new Date(message.receivedAt).toLocaleString()}
								</span>
							</button>
						</li>
					);
				})}
			</ul>
			{menu ? (
				<MessageContextMenu
					open
					x={menu.x}
					y={menu.y}
					onClose={() => setMenu(null)}
					actions={messageActions(
						menu.message,
						setFlags.mutate,
						trash.mutate,
						hub,
						queryClient,
						onPrint,
					)}
				/>
			) : null}
		</>
	);
}

/**
 * The conventional message menu.
 *
 * Entries whose feature does not exist yet are present and disabled, with the reason: §13 asks
 * for the conventional menu per item type, and a menu that grows entries as features land
 * reads as an app that keeps changing shape.
 */
function messageActions(
	message: MessageSummary,
	setFlags: (input: {
		message: MessageSummary;
		isRead: boolean | null;
		isFlagged: boolean | null;
	}) => void,
	trash: (message: MessageSummary) => void,
	hub: HubConnection,
	queryClient: QueryClient,
	onPrint: (message: { id: string; subject: string; from: string }) => void,
): MenuAction[] {
	return [
		{
			label: "Reply",
			run: () => undefined,
			unavailable: "Compose is not built yet.",
		},
		{
			label: "Reply all",
			run: () => undefined,
			unavailable: "Compose is not built yet.",
		},
		{
			label: "Forward",
			run: () => undefined,
			unavailable: "Compose is not built yet.",
		},
		{ label: "-", run: () => undefined },
		{
			label: message.isRead ? "Mark unread" : "Mark read",
			run: () =>
				setFlags({ message, isRead: !message.isRead, isFlagged: null }),
		},
		{
			label: message.isFlagged ? "Remove flag" : "Flag",
			run: () =>
				setFlags({ message, isRead: null, isFlagged: !message.isFlagged }),
		},
		{ label: "-", run: () => undefined },
		{ label: "Move to trash", run: () => trash(message), danger: true },
		{
			label: "Save as .eml",
			run: () => void saveAsEml(hub, message),
		},
		{
			label: "Print",
			run: () => void printMessage(hub, queryClient, message, onPrint),
		},
	];
}

/**
 * Loads the body into the query cache before switching to the reading pane, so it renders
 * already-fetched rather than showing "Downloading this message…" under the print dialog —
 * printing has to go through that same sandboxed render, never a second ad-hoc one (§13).
 */
async function printMessage(
	hub: HubConnection,
	queryClient: QueryClient,
	message: MessageSummary,
	onPrint: (message: { id: string; subject: string; from: string }) => void,
): Promise<void> {
	await queryClient.fetchQuery({
		queryKey: ["body", message.id],
		queryFn: () => hub.invoke("GetMessageBody", message.id),
	});
	onPrint({ ...message, from: senderAddress(message) });
	// One frame so the reading pane has actually mounted the now-cached body before printing.
	requestAnimationFrame(() => window.print());
}

/**
 * Hands the raw MIME to the OS's own save flow rather than opening a bespoke dialog: an
 * anchor with a `blob:` URL and `download` set is what a browser's download manager — which
 * Electron's `BrowserWindow` already runs — is for (§13 Export).
 */
async function saveAsEml(
	hub: HubConnection,
	message: MessageSummary,
): Promise<void> {
	const base64 = await hub.invoke<string>("SaveMessageAsEml", message.id);
	const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
	const blob = new Blob([bytes], { type: "message/rfc822" });
	const url = URL.createObjectURL(blob);
	try {
		const link = document.createElement("a");
		link.href = url;
		link.download = `${message.subject || "message"}.eml`;
		link.click();
	} finally {
		URL.revokeObjectURL(url);
	}
}

/** Desired state wins over server-known state while a mutation is outstanding (§6). */
function isRead(
	message: MessageSummary,
	pending: PendingChange[] | undefined,
): boolean {
	const desired = pending?.find(
		(change) => change.messageId === message.id && change.field === isReadField,
	);
	return desired?.desiredValue ?? message.isRead;
}

function describeSender(message: MessageSummary): string {
	const [first] = message.from;
	if (!first) return "(unknown sender)";
	return first.name ?? first.email;
}

/** The address the remote-content allow list keys on — never the display name. */
function senderAddress(message: MessageSummary): string {
	return message.from[0]?.email ?? "";
}
