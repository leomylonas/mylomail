import type { QueryClient } from "@tanstack/react-query";

/**
 * Runs `release` once every list on screen has been fetched again after `startedAt`, or after
 * `maxWaitMs`, whichever is first.
 *
 * A deleted or moved message is hidden from its list by a claim, and the claim is let go when the
 * server says the change is done. If it is let go before the list has been read again, the list still
 * holds the message and shows it until the read lands: the message "comes back" for a moment, and
 * goes again. The refetch promise alone was not enough to wait on (the claim was released a
 * millisecond after the event, in the diagnostics a user sent), so the lists' own state is checked.
 */
export async function releaseWhenListsAreFresh(
	queryClient: QueryClient,
	startedAt: number,
	release: () => void,
	{
		maxWaitMs = 60_000,
		pollMs = 30,
	}: { maxWaitMs?: number; pollMs?: number } = {},
): Promise<void> {
	const deadline = startedAt + maxWaitMs;
	const unfresh = () =>
		queryClient
			.getQueryCache()
			.findAll({ queryKey: ["messages"], type: "active" })
			.some(
				(query) =>
					!query.isDisabled() &&
					(query.state.fetchStatus !== "idle" ||
						query.state.dataUpdatedAt < startedAt),
			);
	while (unfresh() && Date.now() < deadline)
		await new Promise((resolve) => setTimeout(resolve, pollMs));
	release();
}

interface RefreshState {
	chain: Promise<void>;
	/** The next refresh, not yet begun: a caller that arrives before it begins is covered by it. */
	queued: Promise<void> | null;
}

const refreshStates = new WeakMap<QueryClient, RefreshState>();

/**
 * Reads the message lists again, one read at a time, and at most one more waiting behind it.
 *
 * Every settled change and every message event used to ask for a read at once, and each ask cancelled
 * the read already under way and began another. On a large mailbox a read takes a second or more and
 * the events for a run of deletes arrive over several seconds, so no read ever finished until the
 * events stopped: the lists went on showing what they had held before. Here a read is let finish, and
 * whatever arrived meanwhile is covered by exactly one more after it.
 *
 * @returns resolved when a read that began after this call has finished.
 */
export function refreshMessageLists(queryClient: QueryClient): Promise<void> {
	const state = refreshStates.get(queryClient) ?? {
		chain: Promise.resolve(),
		queued: null,
	};
	refreshStates.set(queryClient, state);
	if (state.queued) return state.queued;
	const next = state.chain.then(async () => {
		state.queued = null;
		await Promise.all([
			queryClient.invalidateQueries(
				{ queryKey: ["messages"] },
				{ cancelRefetch: false },
			),
			queryClient.invalidateQueries(
				{ queryKey: ["search"] },
				{ cancelRefetch: false },
			),
			queryClient.invalidateQueries(
				{ queryKey: ["pending"] },
				{ cancelRefetch: false },
			),
		]).catch(() => undefined);
	});
	state.queued = next;
	state.chain = next;
	return next;
}
