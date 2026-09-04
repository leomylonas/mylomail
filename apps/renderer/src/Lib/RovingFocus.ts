/**
 * Pure index arithmetic for roving-tabindex keyboard navigation over a virtualized list (§13).
 * Kept separate from any one component so it is testable without a DOM/virtualizer harness, and
 * shared by every virtualized list that needs it (MessageList, CalendarAgenda) — the imperative
 * scroll-and-focus side effects live in each component, this only ever answers "given the
 * current focus and a key, what index should be focused next."
 */
export type RovingFocusKey = "ArrowUp" | "ArrowDown" | "Home" | "End";

const rovingFocusKeys: ReadonlySet<string> = new Set([
	"ArrowUp",
	"ArrowDown",
	"Home",
	"End",
]);

export function isRovingFocusKey(key: string): key is RovingFocusKey {
	return rovingFocusKeys.has(key);
}

/**
 * The next focus index for a key press, clamped to the loaded rows — never out of range, and
 * never negative even from an out-of-range `currentIndex` (a row could have been removed by a
 * concurrent mutation since focus last landed on it).
 */
export function nextFocusIndex(
	key: RovingFocusKey,
	currentIndex: number,
	rowCount: number,
): number {
	if (rowCount <= 0) return 0;
	const clamped = Math.min(Math.max(currentIndex, 0), rowCount - 1);

	switch (key) {
		case "ArrowUp":
			return Math.max(clamped - 1, 0);
		case "ArrowDown":
			return Math.min(clamped + 1, rowCount - 1);
		case "Home":
			return 0;
		case "End":
			return rowCount - 1;
	}
}
