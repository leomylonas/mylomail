import { describe, expect, it } from "vitest";
import {
	activityLabel,
	summariseActivity,
} from "@mylomail/renderer/Components/StatusBar/StatusBar";
import {
	CoverageStatus,
	SyncProgressKind,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

const folder = (
	id: string,
	coverage: CoverageStatus,
	fetched: number,
	estimate: number | null,
	overrides: Partial<{
		providerTotalCount: number | null;
		localCount: number;
		isSynthesized: boolean;
	}> = {},
) => ({
	id,
	coverage,
	coverageMessagesFetched: fetched,
	coverageEstimatedTotal: estimate,
	providerTotalCount: estimate,
	localCount: fetched,
	isSynthesized: false,
	...overrides,
});

describe("summariseActivity", () => {
	it("is idle when every folder is done and nothing is indexing", () => {
		expect(
			summariseActivity([], [folder("a", CoverageStatus.Covered, 10, 10)]),
		).toEqual({ syncing: null });
	});

	it("stays active between folders, while later ones have not started", () => {
		// The first folder just finished and the next has not begun: nothing is Backfilling.
		const result = summariseActivity(
			[],
			[
				folder("a", CoverageStatus.Covered, 100, 100),
				folder("b", CoverageStatus.NotStarted, 0, 50),
			],
		);
		expect(result.syncing).toEqual({ fetched: 100, total: 150, folders: 1 });
	});

	it("counts finished folders as complete so the figure only climbs", () => {
		const result = summariseActivity(
			[],
			[
				folder("a", CoverageStatus.Covered, 90, 100),
				folder("b", CoverageStatus.Backfilling, 20, 100),
			],
		);
		expect(result.syncing).toEqual({ fetched: 120, total: 200, folders: 1 });
	});

	it("prefers a live event over the summary for the folder it names", () => {
		const result = summariseActivity(
			[
				{
					mailboxId: "b",
					kind: SyncProgressKind.Coverage,
					messagesFetched: 80,
					estimatedTotal: 100,
				},
			],
			[folder("b", CoverageStatus.Backfilling, 20, 100)],
		);
		expect(result.syncing).toEqual({ fetched: 80, total: 100, folders: 1 });
	});

	it("ignores synthesised label groups, which are never downloaded", () => {
		const result = summariseActivity(
			[],
			[
				folder("g", CoverageStatus.NotStarted, 0, null, {
					isSynthesized: true,
				}),
			],
		);
		expect(result.syncing).toBeNull();
	});

	it("ignores accounts that are already fully downloaded", () => {
		const result = summariseActivity(
			[],
			[
				{
					...folder("a", CoverageStatus.Covered, 5000, 5000),
					accountId: "done",
				},
				{
					...folder("b", CoverageStatus.Backfilling, 20, 100),
					accountId: "busy",
				},
			],
		);
		expect(result.syncing).toEqual({ fetched: 20, total: 100, folders: 1 });
	});

	it("reports a count so far, not a fraction, once a folder passes its provider estimate", () => {
		const result = summariseActivity(
			[],
			[folder("a", CoverageStatus.Backfilling, 2400, 201)],
		);
		expect(result.syncing).toEqual({ fetched: 2400, total: null, folders: 1 });
	});
});

describe("activityLabel", () => {
	const task = (label: string, extra = {}) => ({
		label,
		running: true,
		accountName: undefined,
		done: undefined,
		total: undefined,
		startedAt: undefined,
		...extra,
	});

	it("says exporting, with progress, when an export is all that is running", () => {
		expect(
			activityLabel({
				syncing: false,
				work: [task("Exporting mail", { done: 1900, total: 3064 })],
			}),
		).toBe("Exporting 1,900 of 3,064…");
	});

	it("does not hide ordinary syncing behind the export", () => {
		expect(
			activityLabel({
				syncing: false,
				work: [task("Exporting mail"), task("Syncing the calendar")],
			}),
		).toBe("Syncing and exporting…");
		expect(
			activityLabel({ syncing: true, work: [task("Exporting mail")] }),
		).toBe("Syncing and exporting…");
	});

	it("keeps its wording for mail going out and for plain syncing", () => {
		expect(
			activityLabel({ syncing: false, work: [task("Sending mail")] }),
		).toBe("Sending…");
		expect(activityLabel({ syncing: true, work: [] })).toBe("Syncing…");
		expect(activityLabel({ syncing: false, work: [] })).toBe(
			"Changes waiting…",
		);
	});
});
