import { describe, expect, it } from "vitest";
import { createCalendarFormatters } from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import {
	clockPreferenceFromSetting,
	createDateFormats,
} from "@mylomail/renderer/Lib/DateFormats";

const afternoon = new Date(2026, 9, 13, 16, 5);
const justAfterMidnight = new Date(2026, 9, 13, 0, 5);

describe("the clock preference", () => {
	it("maps the stored ordinals, falling back to the system clock", () => {
		expect(clockPreferenceFromSetting(0)).toBe("system");
		expect(clockPreferenceFromSetting(1)).toBe("12h");
		expect(clockPreferenceFromSetting(2)).toBe("24h");
		expect(clockPreferenceFromSetting(undefined)).toBe("system");
	});

	it.each(["en-US", "de-DE"])(
		"overrides the %s locale's own clock in every kind of time",
		(locale) => {
			const twelve = createDateFormats("12h", locale);
			const twentyFour = createDateFormats("24h", locale);
			expect(twelve.time(afternoon)).toMatch(/4:05\s?PM/i);
			expect(twentyFour.time(afternoon)).toBe("16:05");
			expect(twelve.dateTime(afternoon)).toMatch(/4:05\s?PM/i);
			expect(twentyFour.dateTime(afternoon)).toContain("16:05");
			expect(twelve.dateTimeMedium(afternoon)).toMatch(/4:05\s?PM/i);
			expect(twentyFour.dateTimeMedium(afternoon)).toContain("16:05");
		},
	);

	it("writes just after midnight as 00:05, never 24:05, on a 24-hour clock", () => {
		expect(createDateFormats("24h", "en-US").time(justAfterMidnight)).toMatch(
			/^0?0:05$/,
		);
	});

	it("reaches the calendar's times, hours and reminders too", () => {
		const twelve = createCalendarFormatters("en-US", "12h");
		const twentyFour = createCalendarFormatters("en-US", "24h");
		expect(twelve.time(afternoon)).toMatch(/4:05\s?PM/i);
		expect(twentyFour.time(afternoon)).toBe("16:05");
		expect(twelve.hour(afternoon)).toMatch(/PM/i);
		expect(twentyFour.hour(afternoon)).not.toMatch(/PM/i);
		expect(twelve.reminder(afternoon)).toMatch(/PM/i);
		expect(twentyFour.reminder(afternoon)).toContain("16:05");
	});

	it("leaves a 24-hour locale on its own clock unless told otherwise", () => {
		expect(createCalendarFormatters("de-DE", "system").time(afternoon)).toBe(
			"16:05",
		);
		expect(createCalendarFormatters("de-DE", "12h").time(afternoon)).toMatch(
			/4:05\s?PM/i,
		);
	});
});
