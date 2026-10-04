import type { Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

/**
 * Which of the agenda's `dayCount` consecutive days, counted from `rangeStart`, is `focus`;
 * `-1` when it falls outside them. The agenda scrolls this row to the top and marks it, so the
 * day chosen in the month grid (or the one the header is showing) is the one the list opens on.
 */
export function agendaDayIndex(
	rangeStart: Dayjs,
	focus: Dayjs,
	dayCount: number,
): number {
	const index = focus.startOf("day").diff(rangeStart.startOf("day"), "day");
	return index >= 0 && index < dayCount ? index : -1;
}
