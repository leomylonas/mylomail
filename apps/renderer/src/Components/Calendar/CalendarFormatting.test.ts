import { describe, expect, it } from "vitest";
import {
	calendarGridStart,
	calendarWeekStart,
	createCalendarFormatters,
} from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";

const instant = new Date(2026, 2, 9, 16, 5);

describe("calendar locale formatting", () => {
	it("uses the locale's month, date order, weekday and 24-hour clock", () => {
		const format = createCalendarFormatters("de-DE");

		expect(format.firstDayIndex).toBe(1);
		expect(format.monthYear(instant)).toBe("März 2026");
		expect(format.weekdayShort(instant)).toBe("Mo");
		expect(format.fullDate(instant)).toBe("Montag, 9. März 2026");
		expect(format.time(instant)).toBe("16:05");
	});

	it("retains a locale's Sunday-first calendar and 12-hour clock", () => {
		const format = createCalendarFormatters("en-US");

		expect(format.firstDayIndex).toBe(0);
		expect(format.monthYear(instant)).toBe("March 2026");
		expect(format.time(instant)).toBe("4:05 PM");
	});

	it("formats numbers with the locale's numeral system", () => {
		const format = createCalendarFormatters("ar-EG");
		expect(format.number(1234)).toBe("١٬٢٣٤");
	});

	it("starts the visible grid on the locale's first weekday", () => {
		const march = dayjs("2026-03-15");
		expect(calendarGridStart(march, 0).format("YYYY-MM-DD")).toBe("2026-03-01");
		expect(calendarGridStart(march, 1).format("YYYY-MM-DD")).toBe("2026-02-23");
	});

	it("starts the week on the locale's first weekday", () => {
		const wednesday = dayjs("2026-10-07T15:30:00");
		expect(calendarWeekStart(wednesday, 0).format("YYYY-MM-DD")).toBe(
			"2026-10-04",
		);
		expect(calendarWeekStart(wednesday, 1).format("YYYY-MM-DD")).toBe(
			"2026-10-05",
		);
		// A first day that is already the anchor's own weekday does not step back a week.
		expect(calendarWeekStart(dayjs("2026-10-04"), 0).format("YYYY-MM-DD")).toBe(
			"2026-10-04",
		);
	});

	it("writes a week as a range the locale's way", () => {
		const format = createCalendarFormatters("en-GB");

		expect(
			format.weekRange(new Date(2026, 9, 4), new Date(2026, 9, 10)),
		).toMatch(/^4\s?[–-]\s?10 Oct 2026$/);
		expect(
			format.weekRange(new Date(2026, 9, 29), new Date(2026, 10, 4)),
		).toMatch(/^29 Oct\s?[–-]\s?4 Nov 2026$/);
	});
});

// Midnight and noon are the two readings a 12-hour clock gets wrong when the AM/PM marker is
// missing or clipped: a block drawn from 00:00 to 12:00 must not read "12:00 – 12:00".
describe("calendar time labels", () => {
	// ICU puts a narrow no-break space before AM/PM; the assertions compare plain spaces.
	const plain = (value: string) => value.replace(/\s/gu, " ");
	const at = (hour: number, minute: number) =>
		new Date(2026, 9, 5, hour, minute);

	it("tells midnight from noon on a 12-hour clock, on the hour and on the half", () => {
		const format = createCalendarFormatters("en-US");

		expect(plain(format.time(at(0, 0)))).toBe("12:00 AM");
		expect(plain(format.time(at(0, 30)))).toBe("12:30 AM");
		expect(plain(format.time(at(9, 30)))).toBe("9:30 AM");
		expect(plain(format.time(at(12, 0)))).toBe("12:00 PM");
		expect(plain(format.time(at(12, 30)))).toBe("12:30 PM");
		expect(plain(format.time(at(23, 59)))).toBe("11:59 PM");
	});

	it("writes every time with a two-digit hour on a 24-hour clock, midnight as 00:00", () => {
		for (const locale of ["de-DE", "en-GB", "fr-FR"]) {
			const format = createCalendarFormatters(locale);

			expect(format.time(at(0, 0))).toBe("00:00");
			expect(format.time(at(0, 30))).toBe("00:30");
			expect(format.time(at(9, 30))).toBe("09:30");
			expect(format.time(at(12, 0))).toBe("12:00");
			expect(format.time(at(12, 30))).toBe("12:30");
			expect(format.time(at(23, 59))).toBe("23:59");
		}
	});
});
