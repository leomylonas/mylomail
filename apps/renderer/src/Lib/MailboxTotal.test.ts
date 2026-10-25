import { describe, expect, it } from "vitest";
import { projectTotal } from "@mylomail/renderer/Lib/MailboxTotal";

describe("projectTotal", () => {
	it("shows the provider's total, not how many are held, when the provider reports one", () => {
		expect(projectTotal(10_000, 100, undefined, 0).total).toBe(10_000);
	});

	it("falls with each delete at once and does not rise again when the delete settles", () => {
		const first = projectTotal(500, 100, undefined, 0);
		// Hidden, not yet gone locally.
		expect(projectTotal(500, 100, first.baseline, 2).total).toBe(498);
		// Gone locally, the provider's total not yet re-read.
		expect(projectTotal(500, 98, first.baseline, 0).total).toBe(498);
	});

	it("takes the provider's new total when it is re-read", () => {
		const first = projectTotal(500, 100, undefined, 0);
		const reread = projectTotal(498, 98, first.baseline, 0);
		expect(reread.total).toBe(498);
		expect(projectTotal(498, 99, reread.baseline, 0).total).toBe(499);
	});

	it("counts what is held when the provider gives no total", () => {
		expect(projectTotal(null, 42, undefined, 2).total).toBe(40);
	});
});
