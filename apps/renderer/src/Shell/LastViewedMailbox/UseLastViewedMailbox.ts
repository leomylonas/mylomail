import { useCallback, useEffect, useRef } from "react";
import { useQuery } from "@tanstack/react-query";
import {
	fetchApi,
	fetchApiJson,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { LastViewedMailboxDtoSchema } from "@mylomail/shared-types/Api/Contracts/LastViewedMailboxDto";

const path = "/shell-settings/last-viewed-mailbox";

/**
 * The folder the mail view reopens to (§13 Epic 2): read once when a plain shell window opens,
 * written back whenever the user picks a folder in one. Like the panel layout it is a global
 * default, not shared live state — with several plain windows open the last click wins as the
 * stored value, and no window's own selection ever moves.
 *
 * `enabled` is false for a window opened for a purpose (notification, mailto), which neither
 * reads nor writes it. A failed read behaves as "nothing remembered"; a failed write is
 * dropped, because remembering a folder must never disturb the window using it.
 */
export function useLastViewedMailbox(enabled: boolean) {
	const query = useQuery({
		queryKey: ["shell-settings", "last-viewed-mailbox"],
		queryFn: () => fetchApiJson(path, LastViewedMailboxDtoSchema),
		enabled,
	});

	// What the backend is known to hold, so re-selecting the folder it already has costs nothing.
	const stored = useRef<string | null>(null);
	const latest = useRef<string | null>(null);
	const writing = useRef(false);
	useEffect(() => {
		if (stored.current === null) stored.current = query.data?.mailboxId ?? null;
	}, [query.data]);

	const remember = useCallback(
		(mailboxId: string) => {
			if (!enabled) return;
			latest.current = mailboxId;
			if (writing.current) return;

			// Writes are strictly one at a time, and only the newest pending choice is sent, so
			// a quick run of clicks cannot land out of order and leave an older folder stored.
			writing.current = true;
			void (async () => {
				try {
					while (latest.current !== null && latest.current !== stored.current) {
						const next = latest.current;
						await fetchApi(path, {
							method: "PUT",
							headers: { "Content-Type": "application/json" },
							body: JSON.stringify({ mailboxId: next }),
							keepalive: true,
						});
						stored.current = next;
					}
				} catch {
					// Dropped on purpose; the next selection tries again.
				} finally {
					writing.current = false;
				}
			})();
		},
		[enabled],
	);

	return {
		remembered: query.data,
		/** Whether the remembered folder is known — or will never be asked for. */
		settled: !enabled || !query.isPending,
		remember,
	};
}
