import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { createCalendarFormatters } from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import {
	agendaTimeLabel,
	eventTitle,
	locationText,
	timeRangeLabel,
	titleWithLocation,
} from "@mylomail/renderer/Components/Calendar/CalendarEventText";

const plain = (value: string) => value.replace(/\s/gu, " ");
const us = createCalendarFormatters("en-US");
const de = createCalendarFormatters("de-DE");

// Local wall-clock dates, so the assertions hold in whatever zone the test runs in.
const at = (day: number, hour: number, minute = 0) =>
	new Date(2026, 9, day, hour, minute);

describe("locationText", () => {
	it("is null for a missing, empty or whitespace-only location", () => {
		expect(locationText(null)).toBeNull();
		expect(locationText(undefined)).toBeNull();
		expect(locationText("")).toBeNull();
		expect(locationText(" \t\r\n \n ")).toBeNull();
	});

	it("joins the lines of a multi-line address on one line", () => {
		expect(locationText("107 Mons School Rd\nMons QLD 4556\r\nAustralia")).toBe(
			"107 Mons School Rd, Mons QLD 4556, Australia",
		);
	});

	it("trims and collapses stray spacing without dropping text", () => {
		expect(locationText("  Room   4 \n\n  Level  2  ")).toBe("Room 4, Level 2");
	});
});

describe("titleWithLocation", () => {
	it("appends the location after the title", () => {
		expect(titleWithLocation("Standup", "Room 4")).toBe("Standup · Room 4");
	});

	it("is just the title when the location is blank", () => {
		expect(titleWithLocation("Standup", "  ")).toBe("Standup");
		expect(titleWithLocation("Standup", null)).toBe("Standup");
	});

	it("names an untitled event", () => {
		expect(eventTitle("   ")).toBe("(No title)");
		expect(titleWithLocation("", "Room 4")).toBe("(No title) · Room 4");
	});
});

describe("timeRangeLabel", () => {
	it("prints a one-day range as two times", () => {
		expect(plain(timeRangeLabel(at(5, 9, 30), at(5, 10, 30), us))).toBe(
			"9:30 AM – 10:30 AM",
		);
		expect(timeRangeLabel(at(5, 9, 30), at(5, 10, 30), de)).toBe(
			"09:30 – 10:30",
		);
	});

	it("keeps AM and PM on both ends of a range from midnight to noon", () => {
		expect(plain(timeRangeLabel(at(5, 0), at(5, 12), us))).toBe(
			"12:00 AM – 12:00 PM",
		);
		expect(timeRangeLabel(at(5, 0), at(5, 12), de)).toBe("00:00 – 12:00");
	});

	// "Buderim visit": Fri 12:00 to Mon 12:00. The Monday column draws it from midnight to noon,
	// so the label cannot be two bare, identical-looking "12:00" times.
	it("names the weekday at each end of a range that crosses days", () => {
		const label = plain(timeRangeLabel(at(2, 12), at(5, 12), us));

		expect(label).toBe(
			`${us.weekdayShort(at(2, 12))} 12:00 PM – ${us.weekdayShort(at(5, 12))} 12:00 PM`,
		);
		expect(label).not.toBe("12:00 PM – 12:00 PM");
		expect(timeRangeLabel(at(2, 12), at(5, 12), de)).toBe(
			`${de.weekdayShort(at(2, 12))} 12:00 – ${de.weekdayShort(at(5, 12))} 12:00`,
		);
	});

	it("treats an end at midnight as the end of the evening before", () => {
		expect(plain(timeRangeLabel(at(5, 20), at(6, 0), us))).toBe(
			"8:00 PM – 12:00 AM",
		);
	});

	it("can lead a one-day range with its weekday", () => {
		expect(plain(timeRangeLabel(at(6, 14), at(6, 15, 30), us, true))).toBe(
			`${us.weekdayShort(at(6, 14))} 2:00 PM – 3:30 PM`,
		);
	});
});

describe("agendaTimeLabel", () => {
	const event = (start: Date, end: Date, isAllDay = false) => ({
		start: start.toISOString(),
		end: end.toISOString(),
		isAllDay,
	});
	const trip = event(at(2, 12), at(5, 12));

	it("shows a one-day event's start time", () => {
		expect(
			plain(agendaTimeLabel(event(at(5, 9), at(5, 10)), dayjs(at(5, 0)), us)),
		).toBe("9:00 AM");
	});

	it("shows when a multi-day event starts, covers a whole day, and ends", () => {
		expect(plain(agendaTimeLabel(trip, dayjs(at(2, 0)), us))).toBe("12:00 PM");
		expect(agendaTimeLabel(trip, dayjs(at(3, 0)), us)).toBe("All day");
		expect(agendaTimeLabel(trip, dayjs(at(4, 0)), us)).toBe("All day");
		expect(plain(agendaTimeLabel(trip, dayjs(at(5, 0)), us))).toBe(
			"Until 12:00 PM",
		);
	});

	it("says all day for an all-day event", () => {
		expect(
			agendaTimeLabel(event(at(5, 0), at(6, 0), true), dayjs(at(5, 0)), us),
		).toBe("All day");
	});
});
