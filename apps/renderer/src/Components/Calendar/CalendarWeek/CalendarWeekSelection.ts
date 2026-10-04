import type { Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	atMinute,
	minutesPerDay,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekLayout";

/**
 * Turning a drag (or Shift+Arrow) across the week's time grid into a time range (§13 Epic 7).
 * Pure and in plain numbers — pixels relative to the grid's own origin, minutes from midnight —
 * so snapping, direction, clamping and the click-versus-drag threshold are testable without a
 * DOM, and the component only has to measure the grid and feed this.
 */

/** A position inside the grid: `x` from the left edge of the first day column, `y` from midnight. */
export interface GridPoint {
	x: number;
	y: number;
}

export interface GridMetrics {
	columnWidth: number;
	hourHeight: number;
	columnCount: number;
}

/** A day column and a minute within it (not yet snapped). */
export interface GridCell {
	column: number;
	minute: number;
}

/**
 * A selected range, start before end, possibly across several columns. `endMinute` is in
 * `(0, 1440]`: a range that stops at midnight ends at the bottom of its last column, not the
 * top of the next.
 */
export interface TimeRange {
	startColumn: number;
	startMinute: number;
	endColumn: number;
	endMinute: number;
}

export interface RangeSegment {
	column: number;
	startMinute: number;
	endMinute: number;
}

/** Movement under this, in pixels, is a click that wobbled, not the start of a drag. */
export const dragThreshold = 4;

export function pointToCell(point: GridPoint, metrics: GridMetrics): GridCell {
	const column =
		metrics.columnWidth > 0 ? Math.floor(point.x / metrics.columnWidth) : 0;
	return {
		column: Math.min(Math.max(column, 0), metrics.columnCount - 1),
		minute: Math.min(
			Math.max((point.y / metrics.hourHeight) * 60, 0),
			minutesPerDay,
		),
	};
}

function position(cell: GridCell): number {
	return cell.column * minutesPerDay + cell.minute;
}

function snapped(cell: GridCell, step: number): GridCell {
	return {
		column: cell.column,
		minute: Math.min(Math.round(cell.minute / step) * step, minutesPerDay),
	};
}

/**
 * The range a drag from `origin` to `current` selects, or `null` while it has moved less than
 * `threshold` pixels (a click). Each end snaps to the nearest `step`-minute line, so dragging
 * from the 14:00 line to the 10:00 line gives exactly 14:00 to 10:00 whichever way it went.
 * Both ends are held to the week's columns and the day's 24 hours, and a drag that lands both
 * ends on one line still selects one step rather than nothing.
 */
export function selectionFromDrag(
	origin: GridPoint,
	current: GridPoint,
	metrics: GridMetrics,
	step: number,
	threshold = dragThreshold,
): TimeRange | null {
	if (Math.hypot(current.x - origin.x, current.y - origin.y) < threshold) {
		return null;
	}

	const a = snapped(pointToCell(origin, metrics), step);
	const b = snapped(pointToCell(current, metrics), step);
	let [first, last] = position(a) <= position(b) ? [a, b] : [b, a];

	if (position(first) === position(last)) {
		// Nothing between the two lines: take the step above, or the one below at the day's end.
		return first.minute + step <= minutesPerDay
			? {
					startColumn: first.column,
					startMinute: first.minute,
					endColumn: first.column,
					endMinute: first.minute + step,
				}
			: {
					startColumn: first.column,
					startMinute: first.minute - step,
					endColumn: first.column,
					endMinute: first.minute,
				};
	}

	// The bottom of one column is the top of the next; write each end in the column it is
	// inside of, so no range starts on an empty strip or ends on a zero-height one.
	if (first.minute >= minutesPerDay) {
		first = { column: first.column + 1, minute: 0 };
	}
	if (last.minute <= 0) {
		last = { column: last.column - 1, minute: minutesPerDay };
	}

	return {
		startColumn: first.column,
		startMinute: first.minute,
		endColumn: last.column,
		endMinute: last.minute,
	};
}

/**
 * The range Shift+Arrow selects: the slots from the one the selection started on to the one
 * the cursor is on, both included, whichever comes first. Cell minutes are already on a step.
 */
export function keyboardRange(
	anchor: GridCell,
	cursor: GridCell,
	step: number,
): TimeRange {
	const [first, last] =
		position(anchor) <= position(cursor) ? [anchor, cursor] : [cursor, anchor];
	return {
		startColumn: first.column,
		startMinute: first.minute,
		endColumn: last.column,
		endMinute: Math.min(last.minute + step, minutesPerDay),
	};
}

/** The slice of `range` inside each column it touches, for drawing the selection per column. */
export function rangeSegments(range: TimeRange): RangeSegment[] {
	const segments: RangeSegment[] = [];
	for (let column = range.startColumn; column <= range.endColumn; column += 1) {
		const startMinute = column === range.startColumn ? range.startMinute : 0;
		const endMinute =
			column === range.endColumn ? range.endMinute : minutesPerDay;
		if (endMinute > startMinute) {
			segments.push({ column, startMinute, endMinute });
		}
	}
	return segments;
}

/** The start and end instants of `range` in the week beginning `weekStart`. */
export function rangeDates(
	weekStart: Dayjs,
	range: TimeRange,
): { start: Dayjs; end: Dayjs } {
	return {
		start: atMinute(weekStart.add(range.startColumn, "day"), range.startMinute),
		end: atMinute(weekStart.add(range.endColumn, "day"), range.endMinute),
	};
}

/**
 * How far to scroll per frame while a drag is held near the edge of the visible grid, in
 * pixels (negative is up). Zero in the middle; ramps up over `edge` pixels to `maxStep`, and
 * stays at `maxStep` once the pointer is past the edge — a drag that leaves the viewport keeps
 * scrolling.
 */
export function autoScrollDelta(
	pointerY: number,
	viewportTop: number,
	viewportBottom: number,
	edge = 24,
	maxStep = 20,
): number {
	if (pointerY < viewportTop + edge) {
		const depth = Math.min(1, (viewportTop + edge - pointerY) / edge);
		return -Math.ceil(depth * maxStep);
	}
	if (pointerY > viewportBottom - edge) {
		const depth = Math.min(1, (pointerY - (viewportBottom - edge)) / edge);
		return Math.ceil(depth * maxStep);
	}
	return 0;
}
