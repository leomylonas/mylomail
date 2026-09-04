import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

/**
 * `CalendarEvent.End` is stored exclusive for all-day events (RFC 5545's DTEND convention,
 * already assumed by `CalDavIcs.cs`'s ICS export and by `CalendarAgenda.tsx`'s day-span filter,
 * which excludes an event whose `end` is not strictly after the day being checked) — but the
 * date `<input>` in the event form shows and edits the last day the user actually means to
 * include. Without this adjustment a single-day all-day event round-trips as `Start === End`,
 * a zero-duration span the agenda filter then excludes from every day, including the one the
 * user picked.
 */
export function toInclusiveEndDateInputValue(exclusiveEndIso: string): string {
	return dayjs(exclusiveEndIso).subtract(1, "day").format("YYYY-MM-DD");
}

export function fromInclusiveEndDateInputValue(
	lastDayInclusive: string,
): string {
	if (!lastDayInclusive) return lastDayInclusive;
	return dayjs(lastDayInclusive).add(1, "day").startOf("day").toISOString();
}
