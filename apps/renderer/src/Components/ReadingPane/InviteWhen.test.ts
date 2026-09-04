import { describe, expect, it } from "vitest";
import { describeInviteWhen } from "@mylomail/renderer/Components/ReadingPane/InviteWhen";

describe("describeInviteWhen", () => {
	it("shows a timed invite as a start datetime through an end time", () => {
		const when = describeInviteWhen(
			"2026-03-10T14:00:00.000Z",
			"2026-03-10T15:00:00.000Z",
			false,
		);
		expect(when).toContain("–");
		expect(when).not.toMatch(/12:00:00 AM/);
	});

	it("shows a single-day all-day invite's date once, not twice", () => {
		// Stored exclusive per RFC 5545: a one-day all-day event's end is the *next* day.
		const when = describeInviteWhen(
			"2026-03-10T00:00:00.000Z",
			"2026-03-11T00:00:00.000Z",
			true,
		);
		expect(when).not.toContain("–");
		expect(when).not.toMatch(/12:00:00 AM|AM|PM/);
	});

	it("shows a multi-day all-day invite as a date range using the inclusive last day", () => {
		const when = describeInviteWhen(
			"2026-03-10T00:00:00.000Z",
			"2026-03-13T00:00:00.000Z",
			true,
		);
		expect(when).toContain("–");
		expect(when).not.toMatch(/12:00:00 AM|AM|PM/);
	});

	// Regression: `start` is UTC-midnight, a literal calendar date, not an instant — parsing it
	// in the reader's local zone shifted the displayed date back a day for anyone west of UTC.
	// The expected value is built from explicit local Y/M/D components (`new Date(2026, 2,
	// 10)`), never from a UTC instant reformatted through the local zone — an earlier version of
	// this test derived "expected" that second way, which shifts by the same amount as the bug
	// itself and so passed whether or not the bug was present.
	it("shows the correct single-day date regardless of the viewer's zone", () => {
		const when = describeInviteWhen(
			"2026-03-10T00:00:00.000Z",
			"2026-03-11T00:00:00.000Z",
			true,
		);
		expect(when).toBe(new Date(2026, 2, 10).toLocaleDateString());
	});

	// The above assertion is only a genuine discriminator for a viewer west of UTC, where the
	// bug it guards against actually shifts the display. This test forces exactly that timezone
	// so the regression is caught on any machine running the suite, not only one that happens to
	// sit west of UTC already.
	it("shows the correct single-day date under a forced west-of-UTC viewer timezone", () => {
		const originalTz = process.env.TZ;
		process.env.TZ = "America/Los_Angeles";
		try {
			const when = describeInviteWhen(
				"2026-03-10T00:00:00.000Z",
				"2026-03-11T00:00:00.000Z",
				true,
			);
			expect(when).toBe(new Date(2026, 2, 10).toLocaleDateString());
		} finally {
			process.env.TZ = originalTz;
		}
	});
});
