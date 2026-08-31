import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import styles from "@mylomail/renderer/Components/MessageList/MessageList.module.css";

interface MessageSummary {
	id: string;
	subject: string;
	snippet: string;
	from: { name: string | null; email: string }[];
	receivedAt: string;
	isRead: boolean;
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
	onSelect,
}: {
	hub: HubConnection;
	accountId: string;
	mailboxId: string;
	onSelect: (messageId: string) => void;
}) {
	const queryClient = useQueryClient();

	const messages = useQuery({
		queryKey: queryKeys.messages(mailboxId),
		queryFn: () => hub.invoke<MessageSummary[]>("GetMessages", mailboxId, 100),
	});

	// What the user has asked for and the server has not yet confirmed. Merged over
	// server-known state so a flag they just toggled does not flicker back while its mutation
	// is in flight (§6).
	const pending = useQuery({
		queryKey: queryKeys.pending(accountId),
		queryFn: () =>
			hub.invoke<PendingChange[]>("GetPendingSyncState", accountId),
	});

	const setRead = useMutation({
		mutationFn: (message: MessageSummary) =>
			hub.invoke(
				"SetFlags",
				accountId,
				[message.id],
				!isRead(message, pending.data),
				null,
			),
		onSettled: () =>
			queryClient.invalidateQueries({ queryKey: queryKeys.pending(accountId) }),
	});

	if (messages.isPending) return <SkeletonText paragraph lineCount={6} />;
	if (messages.isError)
		return <p className={styles.empty}>Could not load messages.</p>;
	if (messages.data.length === 0)
		return <p className={styles.empty}>Nothing here yet.</p>;

	return (
		<ul className={styles.list}>
			{messages.data.map((message) => {
				const read = isRead(message, pending.data);
				return (
					<li key={message.id}>
						<button
							type="button"
							className={`${styles.row} ${read ? "" : styles.unread}`}
							onClick={() => {
								onSelect(message.id);
								// Opening a message marks it read, as every mail client does.
								// Already-read messages enqueue nothing: a redundant mutation
								// would still be a real provider call.
								if (!read) setRead.mutate(message);
							}}
						>
							<span>
								{message.subject || "(no subject)"}
								<br />
								<span className={styles.sender}>{describeSender(message)}</span>
							</span>
							<span className={styles.sender}>
								{new Date(message.receivedAt).toLocaleString()}
							</span>
						</button>
					</li>
				);
			})}
		</ul>
	);
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
