import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	fromInclusiveEndDateInputValue,
	toInclusiveEndDateInputValue,
} from "@mylomail/renderer/Components/Calendar/EventModal/AllDayEventEnd";

describe("toInclusiveEndDateInputValue", () => {
	it("shows the day before the stored exclusive end", () => {
		const exclusiveEnd = dayjs("2026-09-06").startOf("day").toISOString();
		expect(toInclusiveEndDateInputValue(exclusiveEnd)).toBe("2026-09-05");
	});
});

describe("fromInclusiveEndDateInputValue", () => {
	it("stores the day after the picked last day", () => {
		const expected = dayjs("2026-09-06").startOf("day").toISOString();
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
