import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	eventOverlapsDay,
	type DatedEvent,
} from "@mylomail/renderer/Components/Calendar/CalendarEventDays";

/**
 * Pure geometry for the week view's time grid (§13 Epic 7), kept out of the component so the
 * parts that are easy to get subtly wrong — overlap lanes, clamping at midnight, snapping a
 * click to a slot — are testable without a DOM.
 *
 * Times are minutes from the local midnight that starts a day column, read off the wall clock,
 * because the grid is 24 equal hourly rows regardless of a daylight-saving day's real length.
 */

export const minutesPerDay = 24 * 60;

/** A click or key lands on a slot this long; a new event starts at the slot's start. */
export const slotMinutes = 30;

/** Shortest a block is drawn, so a 5-minute event is still a readable, clickable target. */
export const minimumBlockMinutes = 30;

export interface DayInterval {
	startMinute: number;
	endMinute: number;
}

export interface LaneSlot {
	/** Zero-based column within its cluster of overlapping blocks. */
	lane: number;
	/** How many columns that cluster needs, so every block in it gets the same width. */
	laneCount: number;
}

/**
 * The part of a timed event that falls on `day`, clamped to that day (an event running past
 * midnight continues in the next column) and widened to the minimum visible height.
 * `null` when the event is not on that day at all.
 */
export function timedBlockInterval(
	event: Pick<DatedEvent, "start" | "end">,
	day: Dayjs,
	minimumMinutes = minimumBlockMinutes,
): DayInterval | null {
	const dayStart = day.startOf("day");
	const nextDayStart = dayStart.add(1, "day");
	const start = dayjs(event.start);
	const end = dayjs(event.end);
	if (!start.isBefore(nextDayStart) || !end.isAfter(dayStart)) return null;

	const startMinute = start.isBefore(dayStart)
		? 0
		: start.hour() * 60 + start.minute();
	const endMinute = end.isBefore(nextDayStart)
		? end.hour() * 60 + end.minute()
		: minutesPerDay;

	return {
		startMinute,
		endMinute: Math.min(
			Math.max(endMinute, startMinute + minimumMinutes),
			minutesPerDay,
		),
	};
}

/**
 * Lays overlapping blocks side by side. Blocks that overlap, directly or through a chain
 * (A meets B, B meets C, A and C do not meet), form one cluster and share its column count;
 * inside a cluster each block takes the first column whose previous block has ended. Blocks
 * that only touch (one ends exactly when the next starts) do not overlap.
 */
export function layoutLanes<T extends DayInterval>(
	blocks: readonly T[],
): Array<T & LaneSlot> {
	const ordered = [...blocks].sort(
		(a, b) => a.startMinute - b.startMinute || b.endMinute - a.endMinute,
	);
	const placed: Array<T & LaneSlot> = [];
	let cluster: Array<{ block: T; lane: number }> = [];
	let laneEnds: number[] = [];
	let clusterEnd = -Infinity;

	const closeCluster = () => {
		for (const { block, lane } of cluster) {
			placed.push({ ...block, lane, laneCount: laneEnds.length });
		}
		cluster = [];
		laneEnds = [];
		clusterEnd = -Infinity;
	};

	for (const block of ordered) {
		if (block.startMinute >= clusterEnd) closeCluster();

		let lane = laneEnds.findIndex((end) => end <= block.startMinute);
		if (lane === -1) {
			lane = laneEnds.length;
			laneEnds.push(block.endMinute);
		} else {
			laneEnds[lane] = block.endMinute;
		}
		cluster.push({ block, lane });
		clusterEnd = Math.max(clusterEnd, block.endMinute);
	}
	closeCluster();

	return placed;
}

export interface ColumnSpan {
	startColumn: number;
	/** Exclusive. */
	endColumn: number;
}

/** The week columns an all-day event covers, or `null` when it is not in this week. */
export function allDayColumns(
	event: DatedEvent,
	weekStart: Dayjs,
	dayCount = 7,
): ColumnSpan | null {
	let first = -1;
	let last = -1;
	for (let column = 0; column < dayCount; column += 1) {
		if (!eventOverlapsDay(event, weekStart.add(column, "day"))) continue;
		if (first === -1) first = column;
		last = column;
	}
	return first === -1 ? null : { startColumn: first, endColumn: last + 1 };
}

/**
 * Stacks all-day spans into rows so none overlaps another in the same row: each span, widest
 * first among those starting together, takes the first row free by its start column. Returns the
 * row of each span, in the order given.
 */
export function packAllDayRows(spans: readonly ColumnSpan[]): number[] {
	const width = (span: ColumnSpan) => span.endColumn - span.startColumn;
	const order = spans
		.map((_, index) => index)
		.sort(
			(a, b) =>
				spans[a].startColumn - spans[b].startColumn ||
				width(spans[b]) - width(spans[a]) ||
				a - b,
		);
	const rowEnds: number[] = [];
	const rows = new Array<number>(spans.length).fill(0);

	for (const index of order) {
		const span = spans[index];
		let row = rowEnds.findIndex((end) => end <= span.startColumn);
		if (row === -1) {
			row = rowEnds.length;
			rowEnds.push(span.endColumn);
		} else {
			rowEnds[row] = span.endColumn;
		}
		rows[index] = row;
	}

	return rows;
}

/** The instant `minute` minutes after the start of `day` on its wall clock (1440 is the next midnight). */
export function atMinute(day: Dayjs, minute: number): Dayjs {
	if (minute >= minutesPerDay) return day.add(1, "day").startOf("day");
	return day
		.startOf("day")
		.hour(Math.floor(minute / 60))
		.minute(minute % 60);
}

/**
 * The start of the `step`-minute slot containing `minuteOfDay` on `day`: snapped down, so a
 * click anywhere in the 10:00–10:30 slot starts at 10:00.
 */
export function slotStart(
	day: Dayjs,
	minuteOfDay: number,
	step = slotMinutes,
): Dayjs {
	const snapped = Math.min(
		Math.max(Math.floor(minuteOfDay / step) * step, 0),
		minutesPerDay - step,
	);
	return atMinute(day, snapped);
}

/**
 * Where the grid first scrolls to: an hour before now when the week contains today, so the
 * current time sits near the top, otherwise the start of a working day.
 */
export function initialScrollMinute(weekStart: Dayjs, now: Dayjs): number {
	const dayOffset = now.startOf("day").diff(weekStart.startOf("day"), "day");
	if (dayOffset < 0 || dayOffset >= 7) return 8 * 60;
	return Math.max(now.hour() * 60 + now.minute() - 60, 0);
}

/** A line of text inside a block (title, time, location), including its leading. */
const blockLineHeight = 15;
const blockVerticalPadding = 4;

/** Narrowest a block can be and still read a second line rather than a few letters. */
const minimumTimeWidth = 64;
const minimumLocationWidth = 72;

export interface BlockContent {
	/** The time range gets its own line under the title. */
	time: boolean;
	/** The location gets a line under the time. */
	location: boolean;
}

/**
 * What a block has room to print besides its title, from its size in pixels (which already
 * follows the zoom: the height is the duration times the hour height). Whole lines only — a
 * line is shown if it fits entirely, never half-clipped — and nothing wider than the block can
 * hold. The title is always there; time and location are tooltip and label text when hidden.
 */
export function blockContent(
	heightPx: number,
	widthPx: number,
	hasLocation: boolean,
): BlockContent {
	const lines = Math.floor((heightPx - blockVerticalPadding) / blockLineHeight);
	const time = lines >= 2 && widthPx >= minimumTimeWidth;
	return {
		time,
		location:
			hasLocation && time && lines >= 3 && widthPx >= minimumLocationWidth,
	};
}

/** All-day rows shown before the rest fold behind a "+N more" control. */
export const allDayRowLimit = 6;

export interface AllDayStrip {
	/** Rows needed to show every all-day event. */
	rowCount: number;
	/** Rows actually drawn: all of them, or the limit while collapsed. */
	visibleRows: number;
	/** Events on rows that are not drawn. */
	hiddenEvents: number;
	/** Whether there are more rows than the limit, so the toggle is offered. */
	overflowing: boolean;
}

/**
 * How tall the all-day strip is, in rows, given each event's packed row. The strip grows to
 * fit every row up to {@link allDayRowLimit}; past that it folds the rest behind a toggle
 * (collapsed) or grows to show them all (expanded).
 */
export function allDayStrip(
	rows: readonly number[],
	expanded: boolean,
	limit = allDayRowLimit,
): AllDayStrip {
	const rowCount = rows.reduce((most, row) => Math.max(most, row + 1), 0);
	const overflowing = rowCount > limit;
	const visibleRows = overflowing && !expanded ? limit : rowCount;
	return {
		rowCount,
		visibleRows,
		hiddenEvents: rows.filter((row) => row >= visibleRows).length,
		overflowing,
	};
}
