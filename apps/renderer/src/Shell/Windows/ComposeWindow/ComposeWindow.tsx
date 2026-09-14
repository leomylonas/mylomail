import { useEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import { Compose } from "@mylomail/renderer/Components/Compose/Compose";
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
	const closeSaver = useRef<(() => Promise<boolean>) | null>(null);

	useEffect(
		() =>
			window.windows?.onComposeCloseRequest(async () =>
				closeSaver.current ? await closeSaver.current() : true,
			),
		[],
	);
	useEffect(() => {
		if (!hub) return;
		const subscription = hub.subscribe(
			"accountRemoved",
			(removedAccountId: string) => {
				if (removedAccountId !== accountId) return;
				closeSaver.current = null;
				window.close();
			},
		);
		return () => subscription.dispose();
	}, [accountId, hub]);
	const drafts = useQuery({
		queryKey: ["drafts", accountId],
		queryFn: () => hub!.getDrafts(accountId),
		enabled: !!hub,
	});
	const draft = drafts.data?.find((candidate) => candidate.id === draftId);

	// Reports this window's own draft the same way AppShell's inline compose pane does, so a
	// different window checks against it before opening the same draft independently — see
	// `draftWindows`'s own remarks in Main.ts for why. Cleared on unmount as a courtesy;
	// closing the window already clears it on the shell's side too.
	useEffect(() => {
		void window.windows?.reportDraftState(draftId);
		return () => void window.windows?.reportDraftState(null);
	}, [draftId]);

	return (
		<div className={styles.window}>
			{hub && draft ? (
				<Compose
					hub={hub}
					accountId={accountId}
					draft={draft}
					onClose={() => window.close()}
					registerCloseSaver={(save) => {
						closeSaver.current = save;
						return () => {
							if (closeSaver.current === save) closeSaver.current = null;
						};
					}}
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
