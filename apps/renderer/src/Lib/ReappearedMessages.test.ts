import { describe, expect, it } from "vitest";
import { findReappeared } from "@mylomail/renderer/Lib/ReappearedMessages";

const ids = (...values: string[]) => new Set(values);

describe("findReappeared", () => {
	it("stays quiet while a message is hidden, and when it has gone from the list", () => {
		expect(findReappeared(ids(), ids("a"), ids("b"), ids()).reappeared).toEqual(
			[],
		);
		const hiding = findReappeared(ids(), ids("a"), ids("b"), ids());
		expect(
			findReappeared(hiding.remembered, ids(), ids("b"), ids()).reappeared,
		).toEqual([]);
	});

	it("reports a message that is shown again once its hiding is lifted", () => {
		const hiding = findReappeared(ids(), ids("a"), ids("b"), ids());
		expect(
			findReappeared(hiding.remembered, ids(), ids("a", "b"), ids()).reappeared,
		).toEqual(["a"]);
	});

	it("does not report one that was meant to come back, such as a failed or undone move", () => {
		const hiding = findReappeared(ids(), ids("a"), ids(), ids());
		expect(
			findReappeared(hiding.remembered, ids(), ids("a"), ids("a")).reappeared,
		).toEqual([]);
	});
});
