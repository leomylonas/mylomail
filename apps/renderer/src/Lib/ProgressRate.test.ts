import { describe, expect, it } from "vitest";
import {
	addSample,
	describeEstimate,
	estimateProgress,
	formatDuration,
} from "@mylomail/renderer/Lib/ProgressRate";

const at = (seconds: number, value: number) => ({ at: seconds * 1000, value });

describe("estimateProgress", () => {
	it("says nothing until readings span a stretch of time", () => {
		expect(estimateProgress([at(0, 10), at(5, 20)], 100)).toEqual({
			kind: "measuring",
		});
	});

	it("works out the rate and the time left from the readings", () => {
		// 60 in 60 seconds is 60 a minute; 240 remain, so four minutes.
		expect(estimateProgress([at(0, 100), at(60, 160)], 400)).toEqual({
			kind: "moving",
			perMinute: 60,
			remainingMs: 240_000,
		});
	});

	it("gives a rate but no time left when the total is unknown", () => {
		expect(estimateProgress([at(0, 0), at(30, 15)], null)).toMatchObject({
			kind: "moving",
			remainingMs: null,
		});
	});

	it("reports a stall only after a minute of no movement, never an infinite estimate", () => {
		expect(estimateProgress([at(0, 5), at(30, 5)], 100)).toEqual({
			kind: "measuring",
		});
		expect(estimateProgress([at(0, 5), at(90, 5)], 100)).toEqual({
			kind: "stalled",
			sinceMs: 90_000,
		});
	});
});

describe("addSample", () => {
	it("drops readings that are too old to describe the current speed", () => {
		const samples = addSample([at(0, 1), at(100, 2)], at(400, 3));
		expect(samples.map((s) => s.value)).toEqual([2, 3]);
	});

	it("ignores a repeat of the same instant", () => {
		expect(addSample([at(1, 1)], at(1, 9))).toEqual([at(1, 1)]);
	});
});

describe("describeEstimate", () => {
	it("reads naturally", () => {
		expect(formatDuration(30_000)).toBe("less than a minute");
		expect(formatDuration(95 * 60_000)).toBe("1 h 35 min");
		expect(
			describeEstimate(
				{ kind: "moving", perMinute: 60, remainingMs: 4 * 60_000 },
				"messages",
			),
		).toBe("About 60 messages/min · roughly 4 min left");
	});
});
