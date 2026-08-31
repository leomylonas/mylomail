import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { Button } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import type { OpenDraft } from "@mylomail/renderer/Components/Compose/Compose";
import styles from "@mylomail/renderer/Components/DraftList/DraftList.module.css";

export function DraftList({
	hub,
	accountId,
	onOpen,
}: {
	hub: HubConnection;
	accountId: string;
	onOpen: (draft: OpenDraft) => void;
}) {
	const drafts = useQuery({
		queryKey: ["drafts", accountId],
		queryFn: () => hub.invoke<OpenDraft[]>("GetDrafts", accountId),
	});

	useEffect(() => {
		const refresh = () => void drafts.refetch();
		hub.on("DraftUpdated", refresh);
		return () => hub.off("DraftUpdated", refresh);
	}, [drafts, hub]);

	if (drafts.isLoading) return <p className={styles.empty}>Loading drafts…</p>;
	if (!drafts.data?.length) return <p className={styles.empty}>No drafts.</p>;
	return (
		<ul className={styles.drafts}>
			{drafts.data.map((draft) => (
				<li key={draft.id}>
					<Button kind="ghost" onClick={() => onOpen(draft)}>
						{draft.subject || "(No subject)"}
					</Button>
				</li>
			))}
		</ul>
	);
}
