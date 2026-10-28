/**
 * Which messages hidden for a delete or move are in the list again without having been meant to
 * return. `hidden` is what is hidden now, `visible` what the list shows, `remembered` the ids seen
 * hidden before (updated here, and returned).
 */
export function findReappeared(
	remembered: ReadonlySet<string>,
	hidden: ReadonlySet<string>,
	visible: ReadonlySet<string>,
	expected: ReadonlySet<string>,
): { reappeared: string[]; remembered: Set<string> } {
	const next = new Set<string>();
	const reappeared: string[] = [];
	for (const id of hidden) next.add(id);
	for (const id of remembered) {
		if (hidden.has(id)) continue;
		// No longer hidden: either it has gone from the list (the delete took effect), or it is back.
		if (visible.has(id) && !expected.has(id)) reappeared.push(id);
	}
	return { reappeared, remembered: next };
}
