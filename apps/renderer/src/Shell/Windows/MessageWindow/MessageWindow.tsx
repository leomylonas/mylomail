import { ReadingPane } from "@mylomail/renderer/Components/ReadingPane/ReadingPane";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import styles from "@mylomail/renderer/Shell/Windows/StandaloneWindow.module.css";

/**
 * A single message, opened in its own window (§13 Epic 10).
 *
 * Its own hub connection, its own query cache — the same per-window rule every window follows
 * (`docs/skills/frontend-shell.md`), just with nothing else in the document beside it.
 */
export function MessageWindow({
	messageId,
	subject,
	senderAddress,
}: {
	messageId: string;
	subject: string;
	/**
	 * The message's first `From` address, carried over the query string from the window that
	 * popped this one out (§13 Epic 5/10) — without it, a sender already on the persisted
	 * remote-content allow list would still be blocked and re-prompted here, contradicting
	 * that allow list's whole point of not asking twice.
	 */
	senderAddress?: string;
}) {
	const { hub, status } = useHub();

	return (
		<div className={styles.window}>
			{hub ? (
				<ReadingPane
					hub={hub}
					messageId={messageId}
					subject={subject}
					senderAddress={senderAddress}
				/>
			) : (
				<p className={styles.status}>
					{status === "failed"
						? "Disconnected from the backend."
						: "Connecting…"}
				</p>
			)}
		</div>
	);
}
