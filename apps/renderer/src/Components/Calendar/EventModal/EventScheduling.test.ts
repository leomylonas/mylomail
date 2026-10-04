import { describe, expect, it } from "vitest";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	formatRecurrenceDateLines,
	fromZonedDateTimeInputValue,
	newEventRange,
	parseRecurrenceDateLines,
	recurrenceRuleLines,
	recurrenceRuleForPreset,
	rezoneInstant,
	scheduleValidationError,
	toZonedDateTimeInputValue,
	toggleAllDay,
} from "@mylomail/renderer/Components/Calendar/EventModal/EventScheduling";

describe("calendar event scheduling", () => {
	it("converts wall time through the selected IANA zone across DST", () => {
		expect(
			fromZonedDateTimeInputValue("2026-03-08T09:00", "America/New_York"),
		).toBe("2026-03-08T13:00:00.000Z");
		expect(
			toZonedDateTimeInputValue("2026-03-08T13:00:00.000Z", "America/New_York"),
		).toBe("2026-03-08T09:00");
	});

	it("rejects a local time skipped by a DST transition", () => {
		expect(() =>
			fromZonedDateTimeInputValue("2026-03-08T02:30", "America/New_York"),
		).toThrow("not a valid local time");
	});

	it("uses the first occurrence of a repeated DST wall time", () => {
		expect(
			fromZonedDateTimeInputValue("2026-11-01T01:30", "America/New_York"),
		).toBe("2026-11-01T05:30:00.000Z");
	});

	it("keeps the displayed wall time when its zone changes", () => {
		expect(
			rezoneInstant(
				"2026-03-08T13:00:00.000Z",
				"America/New_York",
				"Europe/London",
			),
		).toBe("2026-03-08T09:00:00.000Z");
	});

	it("round-trips recurrence sets without collapsing rules, dates, or exclusions", () => {
		expect(
			recurrenceRuleLines("RRULE:FREQ=WEEKLY\nFREQ=MONTHLY;BYDAY=MO"),
		).toEqual(["FREQ=WEEKLY", "FREQ=MONTHLY;BYDAY=MO"]);

		const values = parseRecurrenceDateLines(
			"2026-03-08T09:00\n2026-03-15T09:00",
			"America/New_York",
			false,
		);
		expect(values).toEqual([
			"2026-03-08T13:00:00.000Z",
			"2026-03-15T13:00:00.000Z",
		]);
		expect(formatRecurrenceDateLines(values, "America/New_York", false)).toBe(
			"2026-03-08T09:00\n2026-03-15T09:00",
		);
	});

	it("preserves recurrence date seconds and milliseconds", () => {
		const values = ["2026-03-08T13:00:30.000Z", "2026-03-15T13:00:30.125Z"];
		const formatted = formatRecurrenceDateLines(
			values,
			"America/New_York",
			false,
		);

		expect(formatted).toBe("2026-03-08T09:00:30\n2026-03-15T09:00:30.125");
		expect(
			parseRecurrenceDateLines(formatted, "America/New_York", false),
		).toEqual(values);
	});

	it("builds provider-complete presets from the zoned start date", () => {
		const start = "2026-03-31T15:30:00.000Z";
		expect(recurrenceRuleForPreset("weekly", start, "Asia/Tokyo")).toBe(
			"FREQ=WEEKLY;BYDAY=WE",
		);
		expect(recurrenceRuleForPreset("monthly", start, "Asia/Tokyo")).toBe(
			"FREQ=MONTHLY;BYMONTHDAY=1",
		);
		expect(recurrenceRuleForPreset("yearly", start, "Asia/Tokyo")).toBe(
			"FREQ=YEARLY;BYMONTH=4;BYMONTHDAY=1",
		);
	});
});

describe("scheduleValidationError", () => {
	it("accepts an end after the start on the same day or a later one", () => {
		expect(
			scheduleValidationError("2026-10-06T09:00:00Z", "2026-10-06T10:00:00Z"),
		).toBeNull();
		// Tuesday 14:00 to Thursday 10:00: end's time of day is earlier than the start's.
		expect(
			scheduleValidationError("2026-10-06T14:00:00Z", "2026-10-08T10:00:00Z"),
		).toBeNull();
	});

	it("rejects an end before the start, across days as well as within one", () => {
		expect(
			scheduleValidationError("2026-10-08T10:00:00Z", "2026-10-06T14:00:00Z"),
		).toBe("End must be after start.");
		expect(
			scheduleValidationError("2026-10-06T10:00:00Z", "2026-10-06T09:00:00Z"),
		).toBe("End must be after start.");
	});

	it("rejects an event with no length", () => {
		expect(
			scheduleValidationError("2026-10-06T10:00:00Z", "2026-10-06T10:00:00Z"),
		).toBe("End must be after start.");
	});

	it("rejects values that are not dates", () => {
		expect(scheduleValidationError("", "2026-10-06T10:00:00Z")).toBe(
			"Start and end must be valid dates.",
		);
	});
});

describe("toggleAllDay", () => {
	const timed = {
		isAllDay: false,
		// Fri 2 Oct 12:00 to Mon 5 Oct 12:00, Brisbane (UTC+10, no DST).
		start: "2026-10-02T02:00:00.000Z",
		end: "2026-10-05T02:00:00.000Z",
		startTimeZoneId: "Australia/Brisbane",
		endTimeZoneId: "Australia/Brisbane",
		recurrenceDatesText: "",
		exceptionDatesText: "",
	};

	it("keeps every day of a multi-day timed event when it becomes all-day", () => {
		const allDay = toggleAllDay(timed, true);

		expect(allDay.isAllDay).toBe(true);
		expect(allDay.start).toBe("2026-10-02T00:00:00.000Z");
		// Exclusive end: the day after the last day, Monday the 5th.
		expect(allDay.end).toBe("2026-10-06T00:00:00.000Z");
	});

	it("keeps the first and last day when an all-day event becomes timed again", () => {
		const back = toggleAllDay(toggleAllDay(timed, true), false);

		expect(back.isAllDay).toBe(false);
		expect(back.start).toBe("2026-10-01T23:00:00.000Z"); // Fri 09:00 Brisbane
		expect(back.end).toBe("2026-10-05T00:00:00.000Z"); // Mon 10:00 Brisbane
		expect(scheduleValidationError(back.start, back.end)).toBeNull();
	});

	it("still turns a one-day event into the same one-hour event it always did", () => {
		const oneDay = {
			...timed,
			startTimeZoneId: "Etc/UTC",
			endTimeZoneId: "Etc/UTC",
			start: "2026-10-05T14:30:00.000Z",
			end: "2026-10-05T15:00:00.000Z",
		};
		const allDay = toggleAllDay(oneDay, true);
		const back = toggleAllDay(allDay, false);

		expect(allDay.start).toBe("2026-10-05T00:00:00.000Z");
		expect(allDay.end).toBe("2026-10-06T00:00:00.000Z");
		expect(back.start).toBe("2026-10-05T09:00:00.000Z");
		expect(back.end).toBe("2026-10-05T10:00:00.000Z");
	});

	it("does not count an end exactly at midnight as another day", () => {
		const evening = {
			...timed,
			startTimeZoneId: "Etc/UTC",
			endTimeZoneId: "Etc/UTC",
			start: "2026-10-05T20:00:00.000Z",
			end: "2026-10-06T00:00:00.000Z",
		};

		expect(toggleAllDay(evening, true).end).toBe("2026-10-06T00:00:00.000Z");
	});

	it("moves recurrence date lines between date and date-time forms", () => {
		const withDates = {
			...timed,
			recurrenceDatesText: "2026-10-09T09:00\n2026-10-16T09:00",
		};

		const allDay = toggleAllDay(withDates, true);

		expect(allDay.recurrenceDatesText).toBe("2026-10-09\n2026-10-16");
		expect(toggleAllDay(allDay, false).recurrenceDatesText).toBe(
			"2026-10-09T09:00\n2026-10-16T09:00",
		);
	});
});

describe("newEventRange", () => {
	const clicked = dayjs("2026-10-05T15:20:45.123");
	const format = (value: Dayjs) => value.format("YYYY-MM-DD HH:mm:ss.SSS");

	it("starts a day click at nine in the morning for an hour", () => {
		const { start, end } = newEventRange(clicked);

		expect(format(start)).toBe("2026-10-05 09:00:00.000");
		expect(format(end)).toBe("2026-10-05 10:00:00.000");
	});

	it("starts a slot click exactly at the slot for an hour", () => {
		const { start, end } = newEventRange(clicked.minute(30), { atTime: true });

		expect(format(start)).toBe("2026-10-05 15:30:00.000");
		expect(format(end)).toBe("2026-10-05 16:30:00.000");
	});

	it("keeps a dragged range's own end, even on a later day", () => {
		const start = dayjs("2026-10-06T14:00");
		const end = dayjs("2026-10-08T10:00");

		const range = newEventRange(start, { atTime: true, end });

		expect(format(range.start)).toBe("2026-10-06 14:00:00.000");
		expect(format(range.end)).toBe("2026-10-08 10:00:00.000");
	});

	it("falls back to an hour when the range does not end after its start", () => {
		const start = dayjs("2026-10-06T14:00");

		expect(format(newEventRange(start, { atTime: true, end: start }).end)).toBe(
			"2026-10-06 15:00:00.000",
		);
		expect(
			format(
				newEventRange(start, { atTime: true, end: start.subtract(1, "day") })
					.end,
			),
		).toBe("2026-10-06 15:00:00.000");
	});
});
