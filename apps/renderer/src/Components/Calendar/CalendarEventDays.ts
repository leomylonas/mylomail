import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

export interface DatedEvent {
	start: string;
	end: string;
	isAllDay: boolean;
}

const dateFormat = "YYYY-MM-DD";

/**
 * Whether `event` falls on the local calendar day `day` (§13 Epic 7), shared by the month
 * grid, the week view and the agenda so one event never lands on different days in different
 * views.
 *
 * A timed event is an instant range, compared against the viewer's local day. An all-day event
 * is not an instant range: its dates are stored as literal-calendar-date UTC midnights with an
 * exclusive end (see `AllDayEventEnd.ts`), so they are compared as dates. Treating them as
 * instants would move the event onto the neighbouring day, or onto two days, for anyone not in
 * UTC.
 */
export function eventOverlapsDay(event: DatedEvent, day: Dayjs): boolean {
	if (event.isAllDay) {
		const first = dayjs.utc(event.start).format(dateFormat);
		const exclusiveEnd = dayjs.utc(event.end).format(dateFormat);
		const key = day.format(dateFormat);
		// A degenerate end at or before the start still occupies its own day.
		return exclusiveEnd > first
			? first <= key && key < exclusiveEnd
			: key === first;
	}

	return (
		dayjs(event.start).isBefore(day.endOf("day")) &&
		dayjs(event.end).isAfter(day.startOf("day"))
	);
}
