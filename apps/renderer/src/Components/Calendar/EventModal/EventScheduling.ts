import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

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
