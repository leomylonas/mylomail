import { describe, expect, it } from "vitest";
import { calendarDayFocusTarget } from "@mylomail/renderer/Components/Calendar/CalendarGrid/CalendarGrid";

describe("calendarDayFocusTarget", () => {
	it("moves within the six-week grid with arrow keys", () => {
		expect(calendarDayFocusTarget(8, "ArrowLeft", 42)).toBe(7);
		expect(calendarDayFocusTarget(8, "ArrowRight", 42)).toBe(9);
		expect(calendarDayFocusTarget(8, "ArrowUp", 42)).toBe(1);
		expect(calendarDayFocusTarget(8, "ArrowDown", 42)).toBe(15);
	});

	it("moves to the current week boundaries and keeps focus inside the grid", () => {
		expect(calendarDayFocusTarget(10, "Home", 42)).toBe(7);
		expect(calendarDayFocusTarget(10, "End", 42)).toBe(13);
		expect(calendarDayFocusTarget(0, "ArrowLeft", 42)).toBeNull();
		expect(calendarDayFocusTarget(41, "ArrowRight", 42)).toBeNull();
		expect(calendarDayFocusTarget(0, "Unexpected", 42)).toBeNull();
	});
});
