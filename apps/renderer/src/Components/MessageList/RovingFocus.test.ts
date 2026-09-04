import { describe, expect, it } from "vitest";
import {
	isRovingFocusKey,
	nextFocusIndex,
} from "@mylomail/renderer/Components/MessageList/RovingFocus";

describe("isRovingFocusKey", () => {
	it("recognises the four navigation keys", () => {
		expect(isRovingFocusKey("ArrowUp")).toBe(true);
		expect(isRovingFocusKey("ArrowDown")).toBe(true);
		expect(isRovingFocusKey("Home")).toBe(true);
		expect(isRovingFocusKey("End")).toBe(true);
	});

	it("rejects action-shortcut keys and arbitrary strings", () => {
		expect(isRovingFocusKey("u")).toBe(false);
		expect(isRovingFocusKey("Delete")).toBe(false);
		expect(isRovingFocusKey("Enter")).toBe(false);
		expect(isRovingFocusKey("")).toBe(false);
	});
});

describe("nextFocusIndex", () => {
	it("moves down and up by one, within range", () => {
		expect(nextFocusIndex("ArrowDown", 2, 10)).toBe(3);
		expect(nextFocusIndex("ArrowUp", 2, 10)).toBe(1);
	});

	it("clamps at both ends instead of wrapping", () => {
		expect(nextFocusIndex("ArrowUp", 0, 10)).toBe(0);
		expect(nextFocusIndex("ArrowDown", 9, 10)).toBe(9);
	});

	it("jumps to the first or last loaded row", () => {
		expect(nextFocusIndex("Home", 5, 10)).toBe(0);
		expect(nextFocusIndex("End", 5, 10)).toBe(9);
	});

	it("clamps an out-of-range current index instead of producing a negative or overflowing result", () => {
		// A row that was focused could have been removed by a concurrent mutation, leaving
		// currentIndex pointing past the now-shorter list.
		expect(nextFocusIndex("ArrowDown", 50, 10)).toBe(9);
		expect(nextFocusIndex("ArrowUp", -3, 10)).toBe(0);
	});

	it("returns 0 for an empty list rather than a negative index", () => {
		expect(nextFocusIndex("ArrowDown", 0, 0)).toBe(0);
		expect(nextFocusIndex("End", 0, 0)).toBe(0);
	});
});
