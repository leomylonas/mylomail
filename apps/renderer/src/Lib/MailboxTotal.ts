/** What was true when the provider last reported a mailbox's total. */
export interface TotalBaseline {
	providerTotal: number;
	localCount: number;
}

/**
 * The number of messages the folder holds, as shown in the list header.
 *
 * The provider's total is the only figure that is right under bounded sync, but it only changes
 * when the folder list is next read, so alone it would stay put after a delete and then jump. The
 * local count moves at once but, under bounded sync, is smaller than the folder. So the provider's
 * total is carried forward by how much the local count has moved since it was reported.
 *
 * @param baseline the local count when this provider total first appeared; a new provider total
 *   (or none yet) starts a new baseline.
 * @param pendingRemovals messages hidden for a delete or move that the local count has not dropped for yet.
 */
export function projectTotal(
	providerTotal: number | null,
	localCount: number,
	baseline: TotalBaseline | undefined,
	pendingRemovals: number,
): { total: number; baseline: TotalBaseline | undefined } {
	if (providerTotal === null) {
		return {
			total: Math.max(0, localCount - pendingRemovals),
			baseline: undefined,
		};
	}
	const current =
		baseline?.providerTotal === providerTotal
			? baseline
			: { providerTotal, localCount };
	const total = Math.max(
		0,
		providerTotal + (localCount - current.localCount) - pendingRemovals,
	);
	return { total, baseline: current };
}
