import { useQuery } from "@tanstack/react-query";
import {
	Compose,
	type OpenDraft,
} from "@mylomail/renderer/Components/Compose/Compose";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import styles from "@mylomail/renderer/Shell/Windows/StandaloneWindow.module.css";

/**
 * A single draft, popped out of its main window into its own (§13 Epic 10).
 *
 * The draft is already saved by the time this window opens — `Compose`'s own "open in new
 * window" action saves it first — so there is always a `draftId` to load back from the server's
 * Drafts mailbox rather than from anything passed across the window boundary. That keeps the
 * new window's content authoritative from the server, the same as every other view.
 */
export function ComposeWindow({
	accountId,
	draftId,
}: {
	accountId: string;
	draftId: string;
}) {
	const { hub, status } = useHub();
	const drafts = useQuery({
		queryKey: ["drafts", accountId],
		queryFn: () => hub!.invoke<OpenDraft[]>("GetDrafts", accountId),
		enabled: !!hub,
	});
	const draft = drafts.data?.find((candidate) => candidate.id === draftId);

	return (
		<div className={styles.window}>
			{hub && draft ? (
				<Compose
					hub={hub}
					accountId={accountId}
					draft={draft}
					onClose={() => window.close()}
				/>
			) : (
				<p className={styles.status}>
					{status === "failed"
						? "Disconnected from the backend."
						: drafts.isError
							? "This draft could not be found."
							: "Loading…"}
				</p>
			)}
		</div>
	);
}
