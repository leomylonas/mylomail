import { describe, expect, it } from "vitest";
import { summariseActivity } from "@mylomail/renderer/Components/StatusBar/StatusBar";
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
});
