import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

/**
 * `CalendarEvent.End` is stored exclusive for all-day events (RFC 5545's DTEND convention,
 * already assumed by `CalDavIcs.cs`'s ICS export and by `CalendarAgenda.tsx`'s day-span filter,
 * which excludes an event whose `end` is not strictly after the day being checked) — but the
 * date `<input>` in the event form shows and edits the last day the user actually means to
 * include. Without this adjustment a single-day all-day event round-trips as `Start === End`,
 * a zero-duration span the agenda filter then excludes from every day, including the one the
 * user picked.
 *
 * All-day values are always parsed and formatted in UTC, never the viewer's local zone:
 * `CalDavIcs.cs` stores an all-day date as literal-calendar-date UTC midnight
 * (`new DateTimeOffset(DateTime.ParseExact(value, "yyyyMMdd"), TimeSpan.Zero)`), meant to be
 * read back as that same calendar date everywhere, not as an instant. Formatting it in local
 * time instead shifts the displayed date by one for any viewer west of UTC — a UTC-midnight
 * timestamp falls on the *previous* local calendar day there.
 */
export function toInclusiveEndDateInputValue(exclusiveEndIso: string): string {
	return dayjs.utc(exclusiveEndIso).subtract(1, "day").format("YYYY-MM-DD");
}

export function fromInclusiveEndDateInputValue(
	lastDayInclusive: string,
): string {
	if (!lastDayInclusive) return lastDayInclusive;
	return dayjs.utc(lastDayInclusive).add(1, "day").startOf("day").toISOString();
}
