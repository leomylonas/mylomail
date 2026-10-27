import type { QueryClient } from "@tanstack/react-query";

/** A move or delete of mail that Ctrl+Z can put back: the messages, and where they were. */
export interface UndoableMove {
	/** What was done, in the user's words: "Moved to Trash". */
	description: string;
	accountId: string;
	messageIds: string[];
	/** The folder they were in. Local identity, never a provider id (§1). */
	fromMailboxId: string;
}

const undoKey = ["undo-moves"] as const;
const keep = 20;

/** Cache-only and per window, like the other optimistic state: another window has its own history. */
export function recordMove(queryClient: QueryClient, move: UndoableMove): void {
	if (move.messageIds.length === 0) return;
	queryClient.setQueryData<UndoableMove[]>(undoKey, (current) =>
		[...(current ?? []), move].slice(-keep),
	);
}

/** The latest move, removed from the history; nothing when there is none. */
export function takeLastMove(
	queryClient: QueryClient,
): UndoableMove | undefined {
	const history = queryClient.getQueryData<UndoableMove[]>(undoKey) ?? [];
	const last = history[history.length - 1];
	if (last) queryClient.setQueryData(undoKey, history.slice(0, -1));
	return last;
}
