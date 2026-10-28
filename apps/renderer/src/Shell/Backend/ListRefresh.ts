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
		maxWaitMs = 5000,
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
