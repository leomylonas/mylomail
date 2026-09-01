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
}: {
	messageId: string;
	subject: string;
}) {
	const { hub, status } = useHub();

	return (
		<div className={styles.window}>
			{hub ? (
				<ReadingPane hub={hub} messageId={messageId} subject={subject} />
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
