import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	autoScrollDelta,
	dragThreshold,
	keyboardRange,
	pointToCell,
	rangeDates,
	rangeSegments,
	selectionFromDrag,
	type GridMetrics,
	type GridPoint,
	type TimeRange,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekSelection";

// Seven 100px columns, 50px an hour: x = 100 * column, y = 50 * hour.
const metrics: GridMetrics = {
	columnWidth: 100,
	hourHeight: 50,
	columnCount: 7,
};

function at(column: number, hour: number, extra: Partial<GridPoint> = {}) {
	return {
		x: column * 100 + 30 + (extra.x ?? 0),
		y: hour * 50 + (extra.y ?? 0),
	};
}

function range(
	startColumn: number,
	startMinute: number,
	endColumn: number,
	endMinute: number,
): TimeRange {
	return { startColumn, startMinute, endColumn, endMinute };
}

describe("pointToCell", () => {
	it("reads the day column and the minute off the pointer", () => {
		expect(pointToCell(at(2, 14), metrics)).toEqual({ column: 2, minute: 840 });
		expect(pointToCell({ x: 399, y: 25 }, metrics)).toEqual({
			column: 3,
			minute: 30,
		});
	});

	it("holds a pointer outside the grid to its nearest column and the day's ends", () => {
		expect(pointToCell({ x: -80, y: -20 }, metrics)).toEqual({
			column: 0,
			minute: 0,
		});
		expect(pointToCell({ x: 9000, y: 99999 }, metrics)).toEqual({
			column: 6,
			minute: 1440,
		});
	});
});

describe("selectionFromDrag", () => {
	it("selects from Tuesday 14:00 to Thursday 10:00 across days", () => {
		expect(selectionFromDrag(at(2, 14), at(4, 10), metrics, 30)).toEqual(
			range(2, 14 * 60, 4, 10 * 60),
		);
	});

	it("gives the same range when dragged the other way", () => {
		expect(selectionFromDrag(at(4, 10), at(2, 14), metrics, 30)).toEqual(
			range(2, 14 * 60, 4, 10 * 60),
		);
	});

	it("orders a drag within one day, upward or downward", () => {
		const downward = selectionFromDrag(at(3, 9), at(3, 11), metrics, 30);
		const upward = selectionFromDrag(at(3, 11), at(3, 9), metrics, 30);

		expect(downward).toEqual(range(3, 9 * 60, 3, 11 * 60));
		expect(upward).toEqual(downward);
	});

	it("orders a drag that goes up but also right by position in the week", () => {
		// Later day, earlier hour: Thursday 09:00 is after Tuesday 16:00.
		expect(selectionFromDrag(at(2, 16), at(4, 9), metrics, 30)).toEqual(
			range(2, 16 * 60, 4, 9 * 60),
		);
	});

	it("snaps each end to the nearest line of the current step", () => {
		const origin = at(1, 10, { y: 10 }); // 10:12
		const current = at(1, 12, { y: 43 }); // 12:51.6

		expect(selectionFromDrag(origin, current, metrics, 15)).toEqual(
			range(1, 10 * 60 + 15, 1, 12 * 60 + 45),
		);
		expect(selectionFromDrag(origin, current, metrics, 30)).toEqual(
			range(1, 10 * 60, 1, 13 * 60),
		);
		expect(selectionFromDrag(origin, current, metrics, 60)).toEqual(
			range(1, 10 * 60, 1, 13 * 60),
		);
	});

	it("snaps the same pointer positions differently at each zoom step", () => {
		const origin = at(0, 8, { y: 20 }); // 08:24
		const current = at(0, 9, { y: 40 }); // 09:48

		const startAt = (step: number) =>
			selectionFromDrag(origin, current, metrics, step)?.startMinute;
		const endAt = (step: number) =>
			selectionFromDrag(origin, current, metrics, step)?.endMinute;

		expect([startAt(15), endAt(15)]).toEqual([8 * 60 + 30, 9 * 60 + 45]);
		expect([startAt(30), endAt(30)]).toEqual([8 * 60 + 30, 10 * 60]);
		expect([startAt(60), endAt(60)]).toEqual([8 * 60, 10 * 60]);
	});

	it("clamps a drag that leaves the grid to the week and the day", () => {
		expect(
			selectionFromDrag(at(5, 20), { x: 99999, y: 99999 }, metrics, 30),
		).toEqual(range(5, 20 * 60, 6, 1440));
		expect(
			selectionFromDrag({ x: 120, y: 200 }, { x: -500, y: -500 }, metrics, 30),
		).toEqual(range(0, 0, 1, 4 * 60));
	});

	it("does nothing for a click that moved less than the threshold", () => {
		const origin = at(2, 14);

		expect(selectionFromDrag(origin, origin, metrics, 30)).toBeNull();
		expect(
			selectionFromDrag(
				origin,
				{ x: origin.x + 2, y: origin.y + 2 },
				metrics,
				30,
			),
		).toBeNull();
	});

	it("starts selecting at the threshold, and always once a drag is under way", () => {
		const origin = at(2, 14);
		const nudged = { x: origin.x + dragThreshold, y: origin.y };

		expect(selectionFromDrag(origin, nudged, metrics, 30)).not.toBeNull();
		// With the threshold lifted (a drag already started) even no movement stays a selection.
		expect(selectionFromDrag(origin, origin, metrics, 30, 0)).not.toBeNull();
	});

	it("still selects one step when both ends land on the same line", () => {
		const origin = at(2, 14);
		const sideways = { x: origin.x + 20, y: origin.y };

		expect(selectionFromDrag(origin, sideways, metrics, 30)).toEqual(
			range(2, 14 * 60, 2, 14 * 60 + 30),
		);
		expect(selectionFromDrag(origin, sideways, metrics, 60)).toEqual(
			range(2, 14 * 60, 2, 15 * 60),
		);
	});

	it("selects the last step of the day when both ends land on midnight", () => {
		const bottom = at(2, 24);

		expect(
			selectionFromDrag(bottom, { x: bottom.x + 20, y: bottom.y }, metrics, 30),
		).toEqual(range(2, 1440 - 30, 2, 1440));
	});

	it("writes a range that stops at midnight as the bottom of its column", () => {
		expect(selectionFromDrag(at(2, 14), at(4, 0), metrics, 30)).toEqual(
			range(2, 14 * 60, 3, 1440),
		);
	});
});

describe("keyboardRange", () => {
	it("is the one slot under the cursor when nothing has been extended", () => {
		expect(
			keyboardRange({ column: 1, minute: 600 }, { column: 1, minute: 600 }, 30),
		).toEqual(range(1, 600, 1, 630));
	});

	it("includes the slot the cursor is on when extending down", () => {
		expect(
			keyboardRange({ column: 1, minute: 600 }, { column: 1, minute: 690 }, 30),
		).toEqual(range(1, 600, 1, 720));
	});

	it("extends backwards and across days from either end", () => {
		expect(
			keyboardRange({ column: 3, minute: 600 }, { column: 2, minute: 840 }, 30),
		).toEqual(range(2, 840, 3, 630));
	});

	it("stops at the end of the day", () => {
		expect(
			keyboardRange(
				{ column: 0, minute: 1380 },
				{ column: 0, minute: 1410 },
				30,
			),
		).toEqual(range(0, 1380, 0, 1440));
	});
});

describe("rangeSegments", () => {
	it("is a single slice for a one-day range", () => {
		expect(rangeSegments(range(2, 600, 2, 720))).toEqual([
			{ column: 2, startMinute: 600, endMinute: 720 },
		]);
	});

	it("draws the first day from the start, whole days between, and the last day to the end", () => {
		expect(rangeSegments(range(2, 14 * 60, 4, 10 * 60))).toEqual([
			{ column: 2, startMinute: 840, endMinute: 1440 },
			{ column: 3, startMinute: 0, endMinute: 1440 },
			{ column: 4, startMinute: 0, endMinute: 600 },
		]);
	});

	it("leaves out a column the range only touches at midnight", () => {
		expect(rangeSegments(range(2, 840, 3, 0))).toEqual([
			{ column: 2, startMinute: 840, endMinute: 1440 },
		]);
	});
});

describe("rangeDates", () => {
	const weekStart = dayjs("2026-10-04");

	it("turns columns and minutes into the week's real dates", () => {
		const { start, end } = rangeDates(weekStart, range(2, 14 * 60, 4, 10 * 60));

		expect(start.format("YYYY-MM-DD HH:mm")).toBe("2026-10-06 14:00");
		expect(end.format("YYYY-MM-DD HH:mm")).toBe("2026-10-08 10:00");
	});

	it("ends a range that reaches the bottom of the last column at the next midnight", () => {
		const { end } = rangeDates(weekStart, range(6, 22 * 60, 6, 1440));

		expect(end.format("YYYY-MM-DD HH:mm")).toBe("2026-10-11 00:00");
	});
});

describe("autoScrollDelta", () => {
	const top = 100;
	const bottom = 500;

	it("does not scroll while the pointer is away from the edges", () => {
		expect(autoScrollDelta(300, top, bottom)).toBe(0);
		expect(autoScrollDelta(top + 24, top, bottom)).toBe(0);
		expect(autoScrollDelta(bottom - 24, top, bottom)).toBe(0);
	});

	it("scrolls up near the top edge and down near the bottom edge", () => {
		expect(autoScrollDelta(top + 10, top, bottom)).toBeLessThan(0);
		expect(autoScrollDelta(bottom - 10, top, bottom)).toBeGreaterThan(0);
	});

	it("speeds up the closer the pointer gets, up to a maximum once it is past the edge", () => {
		const slow = Math.abs(autoScrollDelta(top + 20, top, bottom));
		const fast = Math.abs(autoScrollDelta(top + 2, top, bottom));

		expect(fast).toBeGreaterThan(slow);
		expect(autoScrollDelta(top - 300, top, bottom)).toBe(-20);
		expect(autoScrollDelta(bottom + 300, top, bottom)).toBe(20);
	});
});
