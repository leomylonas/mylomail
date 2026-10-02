import { useEffect } from "react";
import { WarningAltFilled } from "@carbon/icons-react";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useQuery } from "@tanstack/react-query";
import { Avatar } from "@mylomail/renderer/Components/Avatar/Avatar";
import type { OpenDraft } from "@mylomail/renderer/Components/Compose/Compose";
import styles from "@mylomail/renderer/Components/DraftList/DraftList.module.css";

/**
 * The contents of the Drafts folder. Drafts are structured authoring documents rather than
 * received messages (§1), so this stands in for `MessageList` when the Drafts folder is
 * selected and opens the draft in compose.
 */
export function DraftList({
	hub,
	accountId,
	selectedDraftId,
	onOpen,
}: {
	hub: MailHubConnection;
	accountId: string;
	selectedDraftId: string | null;
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

	const count = drafts.data?.length ?? 0;
	return (
		<section className={styles.list} aria-label="Drafts">
			<header className={styles.header}>
				<h2>Drafts</h2>
				<span className={styles.count}>
					{count} {count === 1 ? "draft" : "drafts"}
				</span>
			</header>
			{drafts.isLoading ? (
				<p className={styles.empty}>Loading drafts…</p>
			) : drafts.isError ? (
				<p className={styles.empty} role="alert">
					Could not load drafts.
				</p>
			) : count === 0 ? (
				<p className={styles.empty}>No drafts.</p>
			) : (
				<ul className={styles.drafts}>
					{drafts.data!.map((draft) => {
						const recipients = draft.to
							.map((address) => address.name ?? address.email)
							.join(", ");
						return (
							<li key={draft.id}>
								<button
									type="button"
									className={`${styles.row} ${draft.id === selectedDraftId ? styles.selected : ""}`}
									aria-pressed={draft.id === selectedDraftId}
									onClick={() => onOpen(draft)}
								>
									<Avatar name={recipients || "?"} />
									<span className={styles.body}>
										<span className={`${styles.line} ${styles.recipients}`}>
											{recipients || "(No recipients)"}
										</span>
										<span className={styles.line}>
											{draft.subject || "(No subject)"}
										</span>
									</span>
									{draft.syncConflict ? (
										<span
											role="img"
											aria-label="This draft changed on the server — open it to resolve"
											title="This draft changed on the server — open it to resolve"
										>
											<WarningAltFilled size={16} />
										</span>
									) : null}
								</button>
							</li>
						);
					})}
				</ul>
			)}
		</section>
	);
}
