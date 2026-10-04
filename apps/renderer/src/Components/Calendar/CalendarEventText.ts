import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import type { CalendarFormatters } from "@mylomail/renderer/Components/Calendar/CalendarFormatting";

/**
 * The words every calendar view prints for an event (§13 Epic 7): its title, location and
 * time range. Kept in one place so the month grid, week view and agenda cannot disagree about
 * what an event is called, and pure so the cases that read ambiguously (an event that runs past
 * midnight, a location with line breaks) are testable without a DOM.
 */

type TimeFormatters = Pick<CalendarFormatters, "time" | "weekdayShort">;

export function eventTitle(title: string): string {
	return title.trim() || "(No title)";
}

/**
 * A provider's location as one line, or `null` when there is nothing to show. Locations are
 * often multi-line addresses ("107 Mons School Rd\nMons QLD 4556"), so lines are joined with
 * commas; an empty or whitespace-only value is never rendered as a blank label.
 */
export function locationText(
	location: string | null | undefined,
): string | null {
	const lines = (location ?? "")
		.split(/\r?\n/u)
		.map((line) => line.replace(/\s+/gu, " ").trim())
		.filter(Boolean);
	return lines.length > 0 ? lines.join(", ") : null;
}

/** `Title · Location`, or just the title when there is no location. */
export function titleWithLocation(
	title: string,
	location: string | null | undefined,
): string {
	const place = locationText(location);
	return place ? `${eventTitle(title)} · ${place}` : eventTitle(title);
}

/**
 * The days a time range falls on. An end that sits exactly on midnight belongs to the day
 * before — "8 PM to 12 AM" is one evening, not two days.
 */
function lastDayOf(start: Dayjs, end: Dayjs): Dayjs {
	return end.isAfter(start) && end.isSame(end.startOf("day"))
		? end.subtract(1, "millisecond")
		: end;
}

/**
 * `9:30 AM – 10:30 AM` for a range inside one day. When the range crosses days, each end
 * carries its weekday ("Fri 12:00 PM – Mon 12:00 PM"): the bare times alone would read as a
 * single noon-to-noon slot, which is what a week block for the last day of such an event
 * (drawn from midnight to noon) looked like. `leadingDay` adds the weekday to a one-day range
 * as well, for a selection that has no surrounding column to say which day it is.
 */
export function timeRangeLabel(
	start: Date,
	end: Date,
	formatters: TimeFormatters,
	leadingDay = false,
): string {
	const from = dayjs(start);
	const to = dayjs(end);
	if (from.isSame(lastDayOf(from, to), "day")) {
		const times = `${formatters.time(start)} – ${formatters.time(end)}`;
		return leadingDay ? `${formatters.weekdayShort(start)} ${times}` : times;
	}

	return `${formatters.weekdayShort(start)} ${formatters.time(start)} – ${formatters.weekdayShort(end)} ${formatters.time(end)}`;
}

/** What the agenda prints in an event's time column on `day`. */
export function agendaTimeLabel(
	event: { start: string; end: string; isAllDay: boolean },
	day: Dayjs,
	formatters: Pick<CalendarFormatters, "time">,
): string {
	if (event.isAllDay) return "All day";

	const start = dayjs(event.start);
	const end = dayjs(event.end);
	if (start.isSame(day, "day")) return formatters.time(start.toDate());
	if (lastDayOf(start, end).isSame(day, "day")) {
		return `Until ${formatters.time(end.toDate())}`;
	}

	// A day the event covers end to end.
	return "All day";
}
