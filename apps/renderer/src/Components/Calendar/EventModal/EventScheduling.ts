import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { toInclusiveEndDateInputValue } from "@mylomail/renderer/Components/Calendar/EventModal/AllDayEventEnd";

const timedInputPattern =
	/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{3})?)?$/;
const allDayInputPattern = /^\d{4}-\d{2}-\d{2}$/;

export const defaultCalendarTimeZone =
	Intl.DateTimeFormat().resolvedOptions().timeZone || "Etc/UTC";

const supportedValuesOf = (
	Intl as typeof Intl & {
		supportedValuesOf?: (key: "timeZone") => string[];
	}
).supportedValuesOf;

export const calendarTimeZones = [
	...new Set([
		"Etc/UTC",
		defaultCalendarTimeZone,
		...(supportedValuesOf?.("timeZone") ?? []),
	]),
].sort((left, right) => left.localeCompare(right));

export function toZonedDateTimeInputValue(
	instant: string,
	timeZoneId: string,
): string {
	const local = dayjs(instant).tz(timeZoneId);
	return local.millisecond() !== 0
		? local.format("YYYY-MM-DDTHH:mm:ss.SSS")
		: local.second() !== 0
			? local.format("YYYY-MM-DDTHH:mm:ss")
			: local.format("YYYY-MM-DDTHH:mm");
}

export function fromZonedDateTimeInputValue(
	wallTime: string,
	timeZoneId: string,
): string {
	if (!wallTime) return wallTime;
	const expectedFormat = wallTime.includes(".")
		? "YYYY-MM-DDTHH:mm:ss.SSS"
		: wallTime.length === 19
			? "YYYY-MM-DDTHH:mm:ss"
			: "YYYY-MM-DDTHH:mm";
	const naiveUtc = Date.parse(`${wallTime}Z`);
	const sampleDistance = 48 * 60 * 60 * 1000;
	const offsets = new Set(
		[naiveUtc - sampleDistance, naiveUtc, naiveUtc + sampleDistance].map(
			(sample) => dayjs(sample).tz(timeZoneId).utcOffset(),
		),
	);
	const candidates = [...offsets]
		.map((offset) => naiveUtc - offset * 60 * 1000)
		.filter(
			(candidate) =>
				Number.isFinite(candidate) &&
				dayjs(candidate).tz(timeZoneId).format(expectedFormat) === wallTime,
		);
	if (!timedInputPattern.test(wallTime) || candidates.length === 0) {
		throw new Error(
			`'${wallTime}' is not a valid local time in ${timeZoneId}.`,
		);
	}

	// RFC 5545 resolves a repeated fall-back wall time to its first occurrence.
	return dayjs(Math.min(...candidates)).toISOString();
}

/** Changing a zone keeps the wall-clock fields fixed and changes the represented instant. */
export function rezoneInstant(
	instant: string,
	fromTimeZoneId: string,
	toTimeZoneId: string,
): string {
	return fromZonedDateTimeInputValue(
		toZonedDateTimeInputValue(instant, fromTimeZoneId),
		toTimeZoneId,
	);
}

export function recurrenceRuleLines(value: string): string[] {
	return value
		.split(/\r?\n/u)
		.map((line) => line.trim().replace(/^RRULE:/iu, ""))
		.filter(Boolean);
}

export function formatRecurrenceDateLines(
	values: string[],
	timeZoneId: string,
	isAllDay: boolean,
): string {
	return values
		.map((value) =>
			isAllDay
				? dayjs.utc(value).format("YYYY-MM-DD")
				: toZonedDateTimeInputValue(value, timeZoneId),
		)
		.join("\n");
}

export function parseRecurrenceDateLines(
	value: string,
	timeZoneId: string,
	isAllDay: boolean,
): string[] {
	const lines = value
		.split(/\r?\n/u)
		.map((line) => line.trim())
		.filter(Boolean);
	const pattern = isAllDay ? allDayInputPattern : timedInputPattern;
	const parsed = lines.map((line) => {
		if (!pattern.test(line)) {
			throw new Error(
				isAllDay
					? `Recurrence date '${line}' must use YYYY-MM-DD.`
					: `Recurrence date '${line}' must use YYYY-MM-DDTHH:mm with optional seconds and milliseconds.`,
			);
		}
		return isAllDay
			? dayjs.utc(`${line}T00:00:00`).toISOString()
			: fromZonedDateTimeInputValue(line, timeZoneId);
	});
	return [...new Set(parsed)].sort();
}

export function recurrenceRuleForPreset(
	preset: "daily" | "weekly" | "monthly" | "yearly",
	start: string,
	timeZoneId: string,
): string {
	const local = dayjs(start).tz(timeZoneId);
	if (preset === "daily") return "FREQ=DAILY";
	if (preset === "weekly") {
		return `FREQ=WEEKLY;BYDAY=${["SU", "MO", "TU", "WE", "TH", "FR", "SA"][local.day()]}`;
	}
	if (preset === "monthly") {
		return `FREQ=MONTHLY;BYMONTHDAY=${local.date()}`;
	}
	return `FREQ=YEARLY;BYMONTH=${local.month() + 1};BYMONTHDAY=${local.date()}`;
}

/**
 * Why a start and end cannot be saved together, or `null` when they can. The end must come
 * strictly after the start: an event that ends before it begins, or has no length at all, is
 * rejected whether the two fall on one day or several. (An all-day event's stored end is the
 * exclusive day after its last day, so a valid one is always after its start.)
 */
export function scheduleValidationError(
	start: string,
	end: string,
): string | null {
	if (!dayjs(start).isValid() || !dayjs(end).isValid()) {
		return "Start and end must be valid dates.";
	}
	return dayjs(end).isAfter(dayjs(start)) ? null : "End must be after start.";
}

/** The form fields that depend on whether an event is all-day. */
export interface AllDayFields {
	isAllDay: boolean;
	start: string;
	end: string;
	startTimeZoneId: string;
	endTimeZoneId: string;
	recurrenceDatesText: string;
	exceptionDatesText: string;
}

/**
 * Switches an event between timed and all-day without collapsing it to a single day. Going to
 * all-day keeps every day from the start's date to the end's (an end exactly at midnight
 * belongs to the day before); coming back keeps the first and last day, at 09:00 and 10:00 —
 * a one-day event becomes the same one-hour 09:00 event it always did.
 */
export function toggleAllDay<T extends AllDayFields>(
	values: T,
	isAllDay: boolean,
): T {
	if (isAllDay) {
		const startDate = toZonedDateTimeInputValue(
			values.start,
			values.startTimeZoneId,
		).slice(0, 10);
		const endWall = toZonedDateTimeInputValue(values.end, values.endTimeZoneId);
		const endsAtMidnight = /T00:00(?::00(?:\.000)?)?$/u.test(endWall);
		const endDate = endsAtMidnight
			? dayjs.utc(endWall.slice(0, 10)).subtract(1, "day").format("YYYY-MM-DD")
			: endWall.slice(0, 10);
		const start = dayjs.utc(startDate).startOf("day");
		const lastDay = dayjs.utc(endDate < startDate ? startDate : endDate);
		return {
			...values,
			isAllDay: true,
			start: start.toISOString(),
			end: lastDay.add(1, "day").startOf("day").toISOString(),
			recurrenceDatesText: values.recurrenceDatesText.replace(
				/^(\d{4}-\d{2}-\d{2})T.*$/gmu,
				"$1",
			),
			exceptionDatesText: values.exceptionDatesText.replace(
				/^(\d{4}-\d{2}-\d{2})T.*$/gmu,
				"$1",
			),
		};
	}

	const firstDate = dayjs.utc(values.start).format("YYYY-MM-DD");
	const lastDate = toInclusiveEndDateInputValue(values.end);
	const start = fromZonedDateTimeInputValue(
		`${firstDate}T09:00`,
		values.startTimeZoneId,
	);
	return {
		...values,
		isAllDay: false,
		start,
		end:
			lastDate > firstDate
				? fromZonedDateTimeInputValue(`${lastDate}T10:00`, values.endTimeZoneId)
				: dayjs(start).add(1, "hour").toISOString(),
		recurrenceDatesText: values.recurrenceDatesText.replace(
			/^(\d{4}-\d{2}-\d{2})$/gmu,
			"$1T09:00",
		),
		exceptionDatesText: values.exceptionDatesText.replace(
			/^(\d{4}-\d{2}-\d{2})$/gmu,
			"$1T09:00",
		),
	};
}

/**
 * The start and end a new event opens with. A click on a day starts at 09:00; a click on a
 * time slot starts exactly there; a dragged range keeps its own end. Every case defaults to an
 * hour long, and a range that does not end after its start falls back to that default.
 */
export function newEventRange(
	date: Dayjs,
	options: { atTime?: boolean; end?: Dayjs } = {},
): { start: Dayjs; end: Dayjs } {
	const start =
		options.atTime || options.end
			? date.second(0).millisecond(0)
			: date.hour(9).minute(0).second(0).millisecond(0);
	return {
		start,
		end:
			options.end && options.end.isAfter(start)
				? options.end
				: start.add(1, "hour"),
	};
}
