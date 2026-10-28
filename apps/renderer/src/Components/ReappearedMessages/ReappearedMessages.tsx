import { Button } from "@carbon/react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import {
	describeReappearance,
	reappearances,
} from "@mylomail/renderer/Shell/Backend/ActivityTrace";
import styles from "@mylomail/renderer/Components/ReappearedMessages/ReappearedMessages.module.css";

/**
 * Deleted or moved messages that showed in the list again, with the events around each, so a report
 * of one can carry what happened. Nothing here is sent anywhere; it is text to copy.
 */
export function ReappearedMessages() {
	const queryClient = useQueryClient();
	const found = useQuery({
		queryKey: ["reappeared-messages-view"],
		queryFn: () => reappearances(queryClient),
		refetchOnMount: "always",
		staleTime: 0,
	});
	const entries = found.data ?? [];
	return (
		<section aria-labelledby="reappeared-title">
			<h4 id="reappeared-title">Diagnostics</h4>
			{entries.length === 0 ? (
				<p className={styles.hint}>
					No deleted or moved message has reappeared in this window.
				</p>
			) : (
				<>
					<p className={styles.hint}>
						These messages were deleted or moved and then showed in the list
						again. Copy the details into a report.
					</p>
					<Button
						size="sm"
						kind="tertiary"
						onClick={() =>
							void navigator.clipboard.writeText(
								entries.map(describeReappearance).join("\n\n"),
							)
						}
					>
						Copy details
					</Button>
					<ul className={styles.list}>
						{entries.map((entry) => (
							<li key={`${entry.messageId}-${entry.at}`}>
								{new Date(entry.at).toLocaleTimeString()} —{" "}
								{entry.messageId.slice(0, 8)} ({entry.trace.length} events)
							</li>
						))}
					</ul>
				</>
			)}
		</section>
	);
}
