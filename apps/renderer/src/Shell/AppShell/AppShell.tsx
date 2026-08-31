import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { MailboxTree } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { MessageList } from "@mylomail/renderer/Components/MessageList/MessageList";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import styles from "@mylomail/renderer/Shell/AppShell/AppShell.module.css";

interface Account {
	id: string;
	displayName: string;
	emailAddress: string;
}

/**
 * The window's panel layout.
 *
 * Two panels for now, but the split is real: the sidebar and the list read from the same
 * per-window store rather than passing selection down through props, which is what lets a
 * second window hold a different selection without either knowing the other exists.
 */
export function AppShell() {
	const { hub, status } = useHub();
	const store = useWindowStore();
	const selectedAccountId = useStoreValue(store, "selectedAccountId");
	const selectedMailboxId = useStoreValue(store, "selectedMailboxId");
	const sidebarWidth = useStoreValue(store, "sidebarWidth");

	const accounts = useQuery({
		queryKey: ["accounts"],
		queryFn: async (): Promise<Account[]> => {
			// Same-origin, so the launch cookie authenticates this without a token.
			const response = await fetch("/accounts");
			if (!response.ok)
				throw new Error(`accounts responded ${response.status}`);
			return (await response.json()) as Account[];
		},
	});

	// Selecting the only account is not a decision worth making the user repeat.
	useEffect(() => {
		if (!selectedAccountId && accounts.data?.length)
			store.setState("selectedAccountId", accounts.data[0].id);
	}, [accounts.data, selectedAccountId, store]);

	return (
		<div
			className={styles.shell}
			style={{ ["--mylomail-sidebar-width" as string]: `${sidebarWidth}px` }}
		>
			<header className={styles.header}>
				<h1 className={styles.title}>MyloMail</h1>
				<span className={styles.status}>{describe(status, accounts.data)}</span>
			</header>

			<div className={styles.panels}>
				{hub && selectedAccountId ? (
					<MailboxTree hub={hub} accountId={selectedAccountId} />
				) : (
					<div />
				)}
				<div className={styles.reading}>
					{hub && selectedAccountId && selectedMailboxId ? (
						<MessageList
							hub={hub}
							accountId={selectedAccountId}
							mailboxId={selectedMailboxId}
						/>
					) : (
						<p style={{ padding: "1rem" }}>Select a mailbox.</p>
					)}
				</div>
			</div>
		</div>
	);
}

function describe(status: string, accounts: Account[] | undefined): string {
	if (status === "failed") return "Disconnected from the backend.";
	if (status === "connecting") return "Connecting…";
	if (!accounts?.length) return "No accounts yet.";
	return accounts[0].emailAddress;
}
