import { useEffect } from "react";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@carbon/react";
import type { OpenDraft } from "@mylomail/renderer/Components/Compose/Compose";
import styles from "@mylomail/renderer/Components/DraftList/DraftList.module.css";

export function DraftList({
	hub,
	accountId,
	onOpen,
}: {
	hub: MailHubConnection;
	accountId: string;
	onOpen: (draft: OpenDraft) => void;
}) {
	const drafts = useQuery({
		queryKey: ["drafts", accountId],
		queryFn: () => hub.getDrafts(accountId),
	});

	useEffect(() => {
		const refresh = () => void drafts.refetch();
		return hub.subscribe("draftUpdated", refresh).dispose;
	}, [drafts, hub]);

	if (drafts.isLoading) return <p className={styles.empty}>Loading drafts…</p>;
	if (drafts.isError)
		return (
			<p className={styles.empty} role="alert">
				Could not load drafts.
			</p>
		);
	if (!drafts.data?.length) return <p className={styles.empty}>No drafts.</p>;
	return (
		<ul className={styles.drafts}>
			{drafts.data.map((draft) => (
				<li key={draft.id}>
					<Button kind="ghost" onClick={() => onOpen(draft)}>
						{draft.subject || "(No subject)"}
						{draft.syncConflict ? (
							<span
								role="img"
								aria-label="This draft changed on the server — open it to resolve"
								title="This draft changed on the server — open it to resolve"
							>
								{" "}
								⚠️
							</span>
						) : null}
					</Button>
				</li>
			))}
		</ul>
	);
}
