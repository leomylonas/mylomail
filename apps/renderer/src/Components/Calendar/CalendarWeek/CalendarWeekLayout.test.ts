import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	allDayColumns,
	allDayRowLimit,
	allDayStrip,
	atMinute,
	blockContent,
	initialScrollMinute,
	layoutLanes,
	minimumBlockMinutes,
	minutesPerDay,
	packAllDayRows,
	slotStart,
	timedBlockInterval,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekLayout";

interface TestBlock {
	id: string;
	startMinute: number;
	endMinute: number;
}

function block(id: string, startHour: number, endHour: number): TestBlock {
	return { id, startMinute: startHour * 60, endMinute: endHour * 60 };
}

function lanes(blocks: TestBlock[]) {
	return Object.fromEntries(
		layoutLanes(blocks).map((placed) => [
			placed.id,
			[placed.lane, placed.laneCount],
		]),
	);
}

describe("layoutLanes", () => {
	it("gives blocks that do not overlap a full-width column each", () => {
		expect(lanes([block("a", 9, 10), block("b", 11, 12)])).toEqual({
			a: [0, 1],
			b: [0, 1],
		});
	});

	it("puts partly overlapping blocks side by side", () => {
		expect(lanes([block("a", 9, 11), block("b", 10, 12)])).toEqual({
			a: [0, 2],
			b: [1, 2],
		});
	});

	it("puts a block nested inside another beside it", () => {
		expect(lanes([block("outer", 9, 13), block("inner", 10, 11)])).toEqual({
			outer: [0, 2],
			inner: [1, 2],
		});
	});

	it("lets a chain share one width, reusing the first column once it is free", () => {
		// a meets b, b meets c, a and c never meet: c goes back under a's column.
		expect(
			lanes([block("a", 9, 11), block("b", 10, 12), block("c", 11, 13)]),
		).toEqual({ a: [0, 2], b: [1, 2], c: [0, 2] });
	});

	it("widens a cluster to as many columns as are ever needed at once", () => {
		expect(
			lanes([block("a", 9, 12), block("b", 9, 12), block("c", 10, 11)]),
		).toEqual({ a: [0, 3], b: [1, 3], c: [2, 3] });
	});

	it("does not treat a block that starts when another ends as overlapping it", () => {
		expect(lanes([block("a", 9, 10), block("b", 10, 11)])).toEqual({
			a: [0, 1],
			b: [0, 1],
		});
	});

	it("does not depend on the order the blocks arrive in", () => {
		expect(
			lanes([block("c", 11, 13), block("a", 9, 11), block("b", 10, 12)]),
		).toEqual({ a: [0, 2], b: [1, 2], c: [0, 2] });
	});

	it("keeps each block's own fields", () => {
		const [placed] = layoutLanes([{ ...block("a", 9, 10), title: "Standup" }]);

		expect(placed).toMatchObject({ title: "Standup", startMinute: 540 });
	});
});

function local(value: string): string {
	return dayjs(value).toISOString();
}

describe("timedBlockInterval", () => {
	const monday = dayjs("2026-10-05");

	it("reads the start and end off the day's wall clock", () => {
		expect(
			timedBlockInterval(
				{ start: local("2026-10-05T09:15"), end: local("2026-10-05T10:45") },
				monday,
			),
		).toEqual({ startMinute: 9 * 60 + 15, endMinute: 10 * 60 + 45 });
	});

	it("clamps an event that runs past midnight to each day it touches", () => {
		const overnight = {
			start: local("2026-10-05T22:00"),
			end: local("2026-10-06T02:30"),
		};

		expect(timedBlockInterval(overnight, monday)).toEqual({
			startMinute: 22 * 60,
			endMinute: minutesPerDay,
		});
		expect(timedBlockInterval(overnight, monday.add(1, "day"))).toEqual({
			startMinute: 0,
			endMinute: 2 * 60 + 30,
		});
	});

	it("fills the whole of a day an event spans end to end", () => {
		const span = {
			start: local("2026-10-04T18:00"),
			end: local("2026-10-07T06:00"),
		};

		expect(timedBlockInterval(span, monday)).toEqual({
			startMinute: 0,
			endMinute: minutesPerDay,
		});
	});

	it("is null for a day the event does not touch, including the one it ends at midnight", () => {
		const evening = {
			start: local("2026-10-05T20:00"),
			end: local("2026-10-06T00:00"),
		};

		expect(timedBlockInterval(evening, monday.add(1, "day"))).toBeNull();
		expect(timedBlockInterval(evening, monday.subtract(1, "day"))).toBeNull();
	});

	it("widens a very short event to the minimum visible height", () => {
		const interval = timedBlockInterval(
			{ start: local("2026-10-05T10:00"), end: local("2026-10-05T10:05") },
			monday,
		);

		expect(interval).toEqual({
			startMinute: 600,
			endMinute: 600 + minimumBlockMinutes,
		});
	});

	it("never widens a block past the end of the day", () => {
		const interval = timedBlockInterval(
			{ start: local("2026-10-05T23:50"), end: local("2026-10-05T23:55") },
			monday,
		);

		expect(interval?.endMinute).toBe(minutesPerDay);
	});
});

describe("allDayColumns", () => {
	const weekStart = dayjs("2026-10-04");

	it("spans the week columns of a multi-day event, end exclusive", () => {
		const trip = {
			start: "2026-10-06T00:00:00.000Z",
			end: "2026-10-09T00:00:00.000Z",
			isAllDay: true,
		};

		expect(allDayColumns(trip, weekStart)).toEqual({
			startColumn: 2,
			endColumn: 5,
		});
	});

	it("clips an event that began before the week and runs past it", () => {
		const long = {
			start: "2026-09-28T00:00:00.000Z",
			end: "2026-10-20T00:00:00.000Z",
			isAllDay: true,
		};

		expect(allDayColumns(long, weekStart)).toEqual({
			startColumn: 0,
			endColumn: 7,
		});
	});

	it("is null outside the week", () => {
		const later = {
			start: "2026-10-11T00:00:00.000Z",
			end: "2026-10-12T00:00:00.000Z",
			isAllDay: true,
		};

		expect(allDayColumns(later, weekStart)).toBeNull();
	});
});

describe("packAllDayRows", () => {
	it("shares a row between spans that do not meet", () => {
		expect(
			packAllDayRows([
				{ startColumn: 0, endColumn: 2 },
				{ startColumn: 2, endColumn: 4 },
			]),
		).toEqual([0, 0]);
	});

	it("stacks overlapping spans, longest first among those starting together", () => {
		expect(
			packAllDayRows([
				{ startColumn: 1, endColumn: 2 },
				{ startColumn: 1, endColumn: 5 },
				{ startColumn: 3, endColumn: 4 },
			]),
		).toEqual([1, 0, 1]);
	});
});

describe("slotStart", () => {
	const day = dayjs("2026-10-05T15:20");

	it("snaps a position inside a slot down to the slot's start", () => {
		expect(slotStart(day, 10 * 60 + 29).format("YYYY-MM-DD HH:mm")).toBe(
			"2026-10-05 10:00",
		);
		expect(slotStart(day, 10 * 60 + 30).format("YYYY-MM-DD HH:mm")).toBe(
			"2026-10-05 10:30",
		);
		expect(slotStart(day, 10 * 60 + 59).format("HH:mm")).toBe("10:30");
	});

	it("stays inside the day at both ends", () => {
		expect(slotStart(day, -15).format("HH:mm")).toBe("00:00");
		expect(slotStart(day, minutesPerDay + 100).format("HH:mm")).toBe("23:30");
	});
});

describe("initialScrollMinute", () => {
	const weekStart = dayjs("2026-10-04");

	it("starts an hour before now when the week contains today", () => {
		expect(initialScrollMinute(weekStart, dayjs("2026-10-07T14:20"))).toBe(
			13 * 60 + 20,
		);
		expect(initialScrollMinute(weekStart, dayjs("2026-10-07T00:30"))).toBe(0);
	});

	it("starts at eight in the morning for any other week", () => {
		expect(initialScrollMinute(weekStart, dayjs("2026-10-14T14:20"))).toBe(
			8 * 60,
		);
		expect(initialScrollMinute(weekStart, dayjs("2026-10-03T23:59"))).toBe(
			8 * 60,
		);
	});
});

describe("slotStart at each zoom step", () => {
	const day = dayjs("2026-10-05T15:20");
	const at = (minute: number, step: number) =>
		slotStart(day, minute, step).format("HH:mm");

	it("snaps the same position to a quarter hour, half hour or hour", () => {
		const minute = 10 * 60 + 44;

		expect(at(minute, 15)).toBe("10:30");
		expect(at(minute, 30)).toBe("10:30");
		expect(at(minute, 60)).toBe("10:00");
		expect(at(10 * 60 + 46, 15)).toBe("10:45");
	});

	it("keeps the last slot of the day a whole slot", () => {
		expect(at(minutesPerDay + 50, 15)).toBe("23:45");
		expect(at(minutesPerDay + 50, 60)).toBe("23:00");
	});
});

describe("atMinute", () => {
	const day = dayjs("2026-10-05T15:20");

	it("reads minutes from the day's midnight on the wall clock", () => {
		expect(atMinute(day, 14 * 60 + 15).format("YYYY-MM-DD HH:mm")).toBe(
			"2026-10-05 14:15",
		);
	});

	it("takes the end of the day to be the next midnight", () => {
		expect(atMinute(day, minutesPerDay).format("YYYY-MM-DD HH:mm")).toBe(
			"2026-10-06 00:00",
		);
	});
});

describe("timedBlockInterval at other zoom levels", () => {
	it("widens a short event to the minimum the zoom asks for", () => {
		const brief = {
			start: dayjs("2026-10-05T10:00").toISOString(),
			end: dayjs("2026-10-05T10:05").toISOString(),
		};
		const day = dayjs("2026-10-05");

		expect(timedBlockInterval(brief, day, 15)?.endMinute).toBe(615);
		expect(timedBlockInterval(brief, day, 100)?.endMinute).toBe(700);
	});

	// Zoomed out, a minimum-height block spans more than the default 30 minutes. Lanes laid out
	// with the stretched end keep a following event from being drawn over it.
	it("lets the lane layout see the stretched end", () => {
		const day = dayjs("2026-10-05");
		const first = timedBlockInterval(
			{
				start: dayjs("2026-10-05T10:00").toISOString(),
				end: dayjs("2026-10-05T10:10").toISOString(),
			},
			day,
			100,
		);
		const second = timedBlockInterval(
			{
				start: dayjs("2026-10-05T10:40").toISOString(),
				end: dayjs("2026-10-05T11:10").toISOString(),
			},
			day,
			100,
		);

		const placed = layoutLanes([
			{ id: "first", ...first! },
			{ id: "second", ...second! },
		]);

		expect(
			placed.map((block) => [block.id, block.lane, block.laneCount]),
		).toEqual([
			["first", 0, 2],
			["second", 1, 2],
		]);
	});
});

describe("blockContent", () => {
	it("shows only the title in a block too short for a second line", () => {
		expect(blockContent(18, 200, true)).toEqual({
			time: false,
			location: false,
		});
		expect(blockContent(20, 200, true)).toEqual({
			time: false,
			location: false,
		});
	});

	it("adds the time once two whole lines fit", () => {
		expect(blockContent(34, 200, true)).toEqual({
			time: true,
			location: false,
		});
	});

	it("adds the location once three whole lines fit", () => {
		expect(blockContent(49, 200, true)).toEqual({ time: true, location: true });
		expect(blockContent(120, 200, true)).toEqual({
			time: true,
			location: true,
		});
	});

	it("never shows a location the event does not have", () => {
		expect(blockContent(120, 200, false)).toEqual({
			time: true,
			location: false,
		});
	});

	it("drops lines the block is too narrow to read", () => {
		expect(blockContent(120, 60, true)).toEqual({
			time: false,
			location: false,
		});
		expect(blockContent(120, 68, true)).toEqual({
			time: true,
			location: false,
		});
		expect(blockContent(120, 72, true)).toEqual({ time: true, location: true });
	});

	it("follows the zoom through the block's height", () => {
		// A one-hour event: 18px an hour at fit, 128px an hour fully zoomed in.
		expect(blockContent(18, 150, true).time).toBe(false);
		expect(blockContent(128, 150, true)).toEqual({
			time: true,
			location: true,
		});
	});
});

describe("allDayStrip", () => {
	it("is no rows when there are no all-day events", () => {
		expect(allDayStrip([], false)).toEqual({
			rowCount: 0,
			visibleRows: 0,
			hiddenEvents: 0,
			overflowing: false,
		});
	});

	it("grows to fit every packed row", () => {
		const strip = allDayStrip([0, 1, 1, 2], false);

		expect(strip.rowCount).toBe(3);
		expect(strip.visibleRows).toBe(3);
		expect(strip.hiddenEvents).toBe(0);
		expect(strip.overflowing).toBe(false);
	});

	it("fits exactly the row limit without folding anything", () => {
		const rows = Array.from({ length: allDayRowLimit }, (_, row) => row);

		expect(allDayStrip(rows, false)).toMatchObject({
			visibleRows: allDayRowLimit,
			hiddenEvents: 0,
			overflowing: false,
		});
	});

	it("folds the rows past the limit behind a count until expanded", () => {
		const rows = [0, 1, 2, 3, 4, 5, 6, 7, 7];

		expect(allDayStrip(rows, false)).toEqual({
			rowCount: 8,
			visibleRows: allDayRowLimit,
			hiddenEvents: 3,
			overflowing: true,
		});
		expect(allDayStrip(rows, true)).toEqual({
			rowCount: 8,
			visibleRows: 8,
			hiddenEvents: 0,
			overflowing: true,
		});
	});

	it("packs real spans into the rows it measures", () => {
		const rows = packAllDayRows([
			{ startColumn: 0, endColumn: 7 },
			{ startColumn: 1, endColumn: 3 },
			{ startColumn: 2, endColumn: 4 },
			{ startColumn: 5, endColumn: 6 },
		]);

		expect(rows).toEqual([0, 1, 2, 1]);
		expect(allDayStrip(rows, false).rowCount).toBe(3);
	});
});
