import { describe, expect, it } from "vitest";
import {
	calendarGridStart,
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
});
