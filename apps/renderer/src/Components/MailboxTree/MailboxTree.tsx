import { useQuery } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { SkeletonText } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import styles from "@mylomail/renderer/Components/MailboxTree/MailboxTree.module.css";

interface Mailbox {
	id: string;
	name: string;
	providerTotalCount: number | null;
	providerUnreadCount: number | null;
	localCount: number;
}

export function MailboxTree({
	hub,
	accountId,
}: {
	hub: HubConnection;
	accountId: string;
}) {
	const store = useWindowStore();
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");

	const mailboxes = useQuery({
		queryKey: queryKeys.mailboxes(accountId),
		queryFn: () => hub.invoke<Mailbox[]>("GetMailboxes", accountId),
	});

	if (mailboxes.isPending) return <SkeletonText paragraph lineCount={5} />;
	if (mailboxes.isError) return <p>Could not load mailboxes.</p>;

	return (
		<nav className={styles.tree} aria-label="Mailboxes">
			<ul>
				{mailboxes.data.map((mailbox) => (
					<li key={mailbox.id}>
						<button
							type="button"
							className={`${styles.item} ${mailbox.id === selectedMailboxId ? styles.selected : ""}`}
							aria-current={mailbox.id === selectedMailboxId}
							onClick={() => store.setState("selectedMailboxId", mailbox.id)}
						>
							<span>{mailbox.name}</span>
							<span className={styles.count}>{describeCount(mailbox)}</span>
						</button>
					</li>
				))}
			</ul>
		</nav>
	);
}

/**
 * The provider's count where there is one, and the local count otherwise.
 *
 * Under a bounded sync the local count is simply wrong as a mailbox total — "last 3 months"
 * of a large inbox holds a fraction of it — so where the server tells us, that is what the
 * sidebar shows. Where it cannot, the local count is shown as what it is (§1).
 */
function describeCount(mailbox: Mailbox): string {
	if (mailbox.providerUnreadCount !== null && mailbox.providerUnreadCount > 0) {
		return `${mailbox.providerUnreadCount}`;
	}

	return mailbox.providerTotalCount === null
		? `${mailbox.localCount} held`
		: "";
}
