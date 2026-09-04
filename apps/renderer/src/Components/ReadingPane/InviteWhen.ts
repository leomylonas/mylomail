import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { toInclusiveEndDateInputValue } from "@mylomail/renderer/Components/Calendar/EventModal/AllDayEventEnd";

/**
 * The invite banner's date/time summary line. An all-day invite's `start`/`end` carry midnight
 * timestamps with no meaningful time-of-day, so formatting them like a timed event would print
 * a misleading "12:00:00 AM" — and `end` is stored exclusive (RFC 5545's DTEND convention, the
 * same one the calendar's own event form already adjusts for), so a single-day all-day invite
 * must show its start date once, not start-date-through-the-next-day.
 *
 * `start` is parsed in UTC, not the viewer's local zone, matching `AllDayEventEnd.ts`'s own
 * convention: an all-day value is stored as literal-calendar-date UTC midnight, and reading it
 * in local time would shift the displayed date by one for any viewer west of UTC.
 */
export function describeInviteWhen(
	start: string,
	end: string,
	isAllDay: boolean,
): string {
	if (!isAllDay) {
		return `${new Date(start).toLocaleString()} – ${new Date(end).toLocaleTimeString()}`;
	}

	const startDate = dayjs.utc(start).format("YYYY-MM-DD");
	const lastDayInclusive = toInclusiveEndDateInputValue(end);
	const startLabel = dayjs(startDate).toDate().toLocaleDateString();
	return startDate === lastDayInclusive
		? startLabel
		: `${startLabel} – ${dayjs(lastDayInclusive).toDate().toLocaleDateString()}`;
}
