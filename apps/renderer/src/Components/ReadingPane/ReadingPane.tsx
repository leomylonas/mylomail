import { useQuery } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText } from "@carbon/react";
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
			{body.data ? <Body body={body.data} /> : null}
		</article>
	);
}

function Body({ body }: { body: MessageBody }) {
	if (!body.isFetched)
		return <p className={styles.waiting}>Downloading this message…</p>;

	if (body.text) return <div className={styles.body}>{body.text}</div>;

	// Markup is not rendered yet: displaying remote-authored HTML needs the sanitising and
	// remote-content policy from §13, and showing it unsanitised to save a step here would be
	// the single worst thing this app could do.
	if (body.html)
		return (
			<p className={styles.waiting}>
				This message is HTML only. Rendering it safely is still to come.
			</p>
		);

	return <p className={styles.waiting}>This message has no body.</p>;
}
