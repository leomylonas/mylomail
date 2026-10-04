/**
 * The week view's vertical time scale (§13 Epic 7): how tall an hour is, and everything that
 * follows from it. Pure arithmetic in pixels so the parts that are easy to get subtly wrong — a
 * zoom that must keep the moment under the pointer where it is, the lowest height that still
 * fits 24 hours, which hour labels survive at a given scale — are testable without a DOM.
 */

export const hoursPerDay = 24;

/** An hour's height when the view opens, before any zoom: 3rem at the default root size. */
export const defaultHourHeight = 48;

/** The most an hour can be stretched; enough to read a quarter-hour at a glance. */
export const maxHourHeight = 128;

/** What one press of a zoom button or key multiplies the hour height by. */
export const zoomStepFactor = 1.25;

/** Even a very short viewport gets a usable, non-zero scale. */
const smallestFitHeight = 8;

/**
 * The hour height at which all 24 hours exactly fit in `viewportHeight` — the lowest the view
 * zooms out to. Floored, never rounded up, so the day can never be a pixel taller than the
 * viewport and show a scrollbar for a sliver.
 */
export function fitHourHeight(viewportHeight: number): number {
	return Math.max(smallestFitHeight, Math.floor(viewportHeight / hoursPerDay));
}

/** `requested` held between the fit height and the maximum (fit wins if the screen is huge). */
export function clampHourHeight(
	requested: number,
	fit: number,
	max = maxHourHeight,
): number {
	return Math.min(Math.max(requested, fit), Math.max(max, fit));
}

export function zoomedHourHeight(
	current: number,
	factor: number,
	fit: number,
): number {
	return clampHourHeight(current * factor, fit);
}

/**
 * How far to scroll after the hour height changes so the moment at `anchorOffset` (pixels
 * below the top of the visible time grid — the pointer, or the viewport's middle) is still at
 * that same offset. The moment is read off in hours, which is the one unit both scales share.
 */
export function anchoredScrollTop({
	scrollTop,
	anchorOffset,
	oldHourHeight,
	newHourHeight,
	viewportHeight,
}: {
	scrollTop: number;
	anchorOffset: number;
	oldHourHeight: number;
	newHourHeight: number;
	viewportHeight: number;
}): number {
	const anchoredHour = (scrollTop + anchorOffset) / oldHourHeight;
	const wanted = anchoredHour * newHourHeight - anchorOffset;
	const furthest = Math.max(0, hoursPerDay * newHourHeight - viewportHeight);
	return Math.min(Math.max(wanted, 0), furthest);
}

/**
 * What a wheel event with Ctrl/Cmd held multiplies the hour height by. A mouse wheel notch
 * (~100px) is about a quarter step; a trackpad pinch, which arrives as a stream of small
 * Ctrl+wheel deltas, accumulates smoothly. Bounded so one huge delta cannot fling the scale.
 */
export function wheelZoomFactor(deltaY: number, deltaMode = 0): number {
	const pixels = deltaMode === 1 ? deltaY * 16 : deltaY;
	return Math.min(Math.max(Math.exp(-pixels * 0.0025), 0.5), 2);
}

const labelSteps = [1, 2, 3, 4, 6, 12] as const;

/** Least vertical room, in pixels, between two hour labels. */
const minimumLabelSpacing = 30;

/**
 * Print a label every this many hours: every hour when there is room, thinning to 2, 3, 4, 6
 * or 12 as the scale shrinks so labels never touch.
 */
export function labelStepHours(hourHeight: number): number {
	return (
		labelSteps.find((step) => step * hourHeight >= minimumLabelSpacing) ??
		labelSteps[labelSteps.length - 1]
	);
}

/**
 * The slot a click, drag or key press snaps to: coarser when zoomed out (a half-hour is a
 * sliver) and finer when zoomed in far enough that a quarter-hour is a comfortable target.
 */
export function snapMinutes(hourHeight: number): 15 | 30 | 60 {
	if (hourHeight >= 96) return 15;
	if (hourHeight >= 36) return 30;
	return 60;
}

/** Which lighter gridlines to draw between the hour lines. */
export function minorGridlines(
	hourHeight: number,
): "none" | "half" | "quarter" {
	if (hourHeight >= 112) return "quarter";
	if (hourHeight >= 64) return "half";
	return "none";
}

/** Shortest a block is drawn, in pixels, so even a 5-minute event is a readable target. */
const minimumBlockHeight = 20;

/**
 * The shortest duration, in minutes, a block is laid out as. Lane layout has to use this rather
 * than a fixed 30 minutes: zoomed out, a 20px block covers more than half an hour, and a block
 * the lanes think ends sooner would be drawn over the one below it.
 */
export function minimumBlockMinutesFor(hourHeight: number): number {
	return Math.max(15, Math.ceil((minimumBlockHeight * 60) / hourHeight));
}
