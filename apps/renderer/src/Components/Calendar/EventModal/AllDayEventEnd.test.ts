import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	fromInclusiveEndDateInputValue,
	toInclusiveEndDateInputValue,
} from "@mylomail/renderer/Components/Calendar/EventModal/AllDayEventEnd";

describe("toInclusiveEndDateInputValue", () => {
	it("shows the day before the stored exclusive end", () => {
		const exclusiveEnd = dayjs.utc("2026-09-06").startOf("day").toISOString();
		expect(toInclusiveEndDateInputValue(exclusiveEnd)).toBe("2026-09-05");
	});

	// Regression: the stored value is UTC-midnight, a literal calendar date, not an instant —
	// formatting it in the reader's local zone shifted the displayed date by one for anyone
	// west of UTC. A plain (non-UTC) dayjs().format() would fail this in that zone.
	it("does not shift the date for a viewer west of UTC", () => {
		const exclusiveEnd = "2026-09-06T00:00:00.000Z";
		expect(toInclusiveEndDateInputValue(exclusiveEnd)).toBe("2026-09-05");
	});
});

describe("fromInclusiveEndDateInputValue", () => {
	it("stores the day after the picked last day", () => {
		const expected = dayjs.utc("2026-09-06").startOf("day").toISOString();
		expect(fromInclusiveEndDateInputValue("2026-09-05")).toBe(expected);
	});

	it("round-trips through toInclusiveEndDateInputValue", () => {
		const stored = fromInclusiveEndDateInputValue("2026-09-05");
		expect(toInclusiveEndDateInputValue(stored)).toBe("2026-09-05");
	});

	it("passes through an empty value unchanged", () => {
		expect(fromInclusiveEndDateInputValue("")).toBe("");
	});
});
