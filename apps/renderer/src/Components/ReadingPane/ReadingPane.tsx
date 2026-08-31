import { useQuery } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText } from "@carbon/react";
import { MessageHtml } from "@mylomail/renderer/Components/MessageHtml/MessageHtml";
import { AttachmentList } from "@mylomail/renderer/Components/AttachmentList/AttachmentList";
import styles from "@mylomail/renderer/Components/ReadingPane/ReadingPane.module.css";

interface MessageBody {
	messageId: string;
	text: string | null;
	html: string | null;
	isFetched: boolean;
}

/**
 * The selected message's body.
 *
 * Content arrives as background work rather than on open (§1), so "not fetched yet" is an
 * ordinary state with its own message — distinct from a message that genuinely has no body,
 * which would otherwise look identical and leave the user waiting for nothing.
 */
export function ReadingPane({
	hub,
	messageId,
	subject,
}: {
	hub: HubConnection;
	messageId: string;
	subject: string;
}) {
	const body = useQuery({
		queryKey: ["body", messageId],
		queryFn: () => hub.invoke<MessageBody>("GetMessageBody", messageId),
		// Content lands after the message does, so an unfetched body is worth asking about
		// again; a fetched one never changes unless its raw content is replaced.
		refetchInterval: (query) => (query.state.data?.isFetched ? false : 2000),
	});

	return (
		<article className={styles.pane} aria-label="Message">
			<h2 className={styles.subject}>{subject || "(no subject)"}</h2>
			{body.isPending ? <SkeletonText paragraph lineCount={4} /> : null}
			{body.data ? (
				<Body body={body.data} messageId={messageId} hub={hub} />
			) : null}
		</article>
	);
}

function Body({
	body,
	messageId,
	hub,
}: {
	body: MessageBody;
	messageId: string;
	hub: HubConnection;
}) {
	if (!body.isFetched)
		return <p className={styles.waiting}>Downloading this message…</p>;

	// HTML preferred where both exist: it is what the sender composed, and the plain-text
	// alternative is usually a degraded copy of it.
	if (body.html)
		return (
			<>
				<MessageHtml html={body.html} messageId={messageId} />
				<AttachmentList hub={hub} messageId={messageId} />
			</>
		);

	if (body.text)
		return (
			<>
				<div className={styles.body}>{body.text}</div>
				<AttachmentList hub={hub} messageId={messageId} />
			</>
		);

	return (
		<>
			<p className={styles.waiting}>This message has no body.</p>
			<AttachmentList hub={hub} messageId={messageId} />
		</>
	);
}
