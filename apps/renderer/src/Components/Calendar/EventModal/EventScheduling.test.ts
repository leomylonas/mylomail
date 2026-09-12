import { describe, expect, it } from "vitest";
import {
	formatRecurrenceDateLines,
	fromZonedDateTimeInputValue,
	parseRecurrenceDateLines,
	recurrenceRuleLines,
	recurrenceRuleForPreset,
	rezoneInstant,
	toZonedDateTimeInputValue,
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
