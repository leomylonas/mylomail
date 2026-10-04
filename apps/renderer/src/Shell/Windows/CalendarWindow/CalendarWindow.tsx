import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Calendar } from "@mylomail/renderer/Components/Calendar/Calendar";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import { fetchApi } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import type { Account } from "@mylomail/renderer/Types/Account";
import standalone from "@mylomail/renderer/Shell/Windows/StandaloneWindow.module.css";
import styles from "@mylomail/renderer/Shell/Windows/CalendarWindow/CalendarWindow.module.css";

/**
 * One calendar, opened in its own window (§13 Epic 7, Epic 10): the same month grid and
 * agenda as the unified view, restricted to that calendar, without the left panel.
 *
 * Its own hub connection and query cache, like every window (`docs/skills/frontend-shell.md`).
 * Like a message window it sets no title of its own; the calendar's name is the heading of the
 * view itself, in the calendar's colour.
 */
export function CalendarWindow({
	calendarId,
	accountId,
}: {
	calendarId: string;
	accountId: string;
}) {
	const { hub, status } = useHub();
	const [accountRemoved, setAccountRemoved] = useState(false);
	const accounts = useQuery({
		queryKey: ["accounts"],
		queryFn: async (): Promise<Account[]> => {
			const response = await fetchApi("/accounts");
			return (await response.json()) as Account[];
		},
	});

	useEffect(() => {
		if (!hub) return;
		const subscription = hub.subscribe(
			"accountRemoved",
			(removedAccountId: string) => {
				if (removedAccountId === accountId) setAccountRemoved(true);
			},
		);
		return () => subscription.dispose();
	}, [accountId, hub]);

	const account = accounts.data?.find(
		(candidate) => candidate.id === accountId,
	);
	const windowAccounts = useMemo(() => (account ? [account] : []), [account]);

	if (accountRemoved || (accounts.data && !account)) {
		return (
			<p className={standalone.status} role="status">
				The account for this calendar has been removed.
			</p>
		);
	}

	return (
		<div className={standalone.window}>
			{hub && account ? (
				<div className={styles.frame}>
					<Calendar
						hub={hub}
						accounts={windowAccounts}
						onlyCalendarId={calendarId}
					/>
				</div>
			) : (
				<p className={standalone.status}>
					{status === "failed"
						? "Disconnected from the backend."
						: "Connecting…"}
				</p>
			)}
		</div>
	);
}
