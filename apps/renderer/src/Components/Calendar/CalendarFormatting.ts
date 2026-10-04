import type { Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

export interface CalendarFormatters {
	firstDayIndex: number;
	monthYear(date: Date): string;
	weekdayShort(date: Date): string;
	fullDate(date: Date): string;
	monthDay(date: Date): string;
	agendaDay(date: Date): string;
	dayNumber(date: Date): string;
	time(date: Date): string;
	reminder(date: Date): string;
	number(value: number): string;
	hour(date: Date): string;
	weekRange(start: Date, end: Date): string;
}

/**
 * Builds one reusable formatter set from the OS locale. English UI strings stay English, but
 * calendar names, date order, numerals, first weekday and 12/24-hour clock follow the user.
 */
export function createCalendarFormatters(
	locales?: Intl.LocalesArgument,
): CalendarFormatters {
	const resolvedLocale = new Intl.DateTimeFormat(locales).resolvedOptions()
		.locale;
	const locale = new Intl.Locale(resolvedLocale) as Intl.Locale & {
		weekInfo?: { firstDay: number };
	};
	const firstDay = locale.weekInfo?.firstDay ?? 7;
	const monthYear = new Intl.DateTimeFormat(locales, {
		month: "long",
		year: "numeric",
	});
	const weekdayShort = new Intl.DateTimeFormat(locales, { weekday: "short" });
	const fullDate = new Intl.DateTimeFormat(locales, {
		weekday: "long",
		year: "numeric",
		month: "long",
		day: "numeric",
	});
	const monthDay = new Intl.DateTimeFormat(locales, {
		month: "long",
		day: "numeric",
	});
	const agendaDay = new Intl.DateTimeFormat(locales, {
		weekday: "short",
		month: "short",
		day: "numeric",
	});
	const dayNumber = new Intl.DateTimeFormat(locales, { day: "numeric" });
	// A 24-hour locale writes midnight "0:00" with plain `hour: "numeric"`, which reads as a
	// different notation from the "16:05" beside it; padding the hour (and pinning `h23`, since a
	// few locales' `h24` would print midnight as "24:00") keeps every time the same width and
	// unmistakable. A 12-hour locale keeps its own AM/PM marker.
	const uses24HourClock = ["h23", "h24"].includes(
		new Intl.DateTimeFormat(locales, { hour: "numeric" }).resolvedOptions()
			.hourCycle ?? "",
	);
	const time = new Intl.DateTimeFormat(
		locales,
		uses24HourClock
			? { hour: "2-digit", minute: "2-digit", hourCycle: "h23" }
			: { hour: "numeric", minute: "2-digit" },
	);
	const reminder = new Intl.DateTimeFormat(locales, {
		month: "short",
		day: "numeric",
		hour: "numeric",
		minute: "2-digit",
	});
	const number = new Intl.NumberFormat(locales);
	const hour = new Intl.DateTimeFormat(locales, { hour: "numeric" });
	// `formatRange` collapses what the two ends share, so the same-month week reads "4 – 10 Oct
	// 2026" in one locale and "Oct 4 – 10, 2026" in another, as the locale writes it.
	const weekRange = new Intl.DateTimeFormat(locales, {
		day: "numeric",
		month: "short",
		year: "numeric",
	});

	return {
		// Intl.Locale numbers weekdays as Monday=1 through Sunday=7; Dayjs uses Sunday=0.
		firstDayIndex: firstDay % 7,
		monthYear: (date) => monthYear.format(date),
		weekdayShort: (date) => weekdayShort.format(date),
		fullDate: (date) => fullDate.format(date),
		monthDay: (date) => monthDay.format(date),
		agendaDay: (date) => agendaDay.format(date),
		dayNumber: (date) => dayNumber.format(date),
		time: (date) => time.format(date),
		reminder: (date) => reminder.format(date),
		number: (value) => number.format(value),
		hour: (date) => hour.format(date),
		weekRange: (start, end) => weekRange.formatRange(start, end),
	};
}

/** First visible day in a locale-ordered six-week month grid. */
export function calendarGridStart(
	anchor: Dayjs,
	firstDayIndex = calendarFormatters.firstDayIndex,
): Dayjs {
	const monthStart = anchor.startOf("month");
	const daysBeforeFirst = (monthStart.day() - firstDayIndex + 7) % 7;
	return monthStart.subtract(daysBeforeFirst, "day");
}

/** First day of the locale's week containing `anchor`, at the start of that day. */
export function calendarWeekStart(
	anchor: Dayjs,
	firstDayIndex = calendarFormatters.firstDayIndex,
): Dayjs {
	const dayStart = anchor.startOf("day");
	return dayStart.subtract((dayStart.day() - firstDayIndex + 7) % 7, "day");
}

export const calendarFormatters = createCalendarFormatters();
