import { describe, expect, it } from "vitest";
import {
	anchoredScrollTop,
	clampHourHeight,
	defaultHourHeight,
	fitHourHeight,
	hoursPerDay,
	labelStepHours,
	maxHourHeight,
	minimumBlockMinutesFor,
	minorGridlines,
	snapMinutes,
	wheelZoomFactor,
	zoomedHourHeight,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekZoom";

describe("fitHourHeight", () => {
	it("is the tallest whole-pixel hour for which 24 of them fit", () => {
		const fit = fitHourHeight(600);

		expect(fit).toBe(25);
		expect(fit * hoursPerDay).toBeLessThanOrEqual(600);
		expect((fit + 1) * hoursPerDay).toBeGreaterThan(600);
	});

	it("never rounds up past the viewport, so a fitted day has no scrollbar", () => {
		for (const viewport of [431, 575, 599, 600, 601, 899]) {
			expect(fitHourHeight(viewport) * hoursPerDay).toBeLessThanOrEqual(
				viewport,
			);
		}
	});

	it("keeps a usable scale in a tiny viewport", () => {
		expect(fitHourHeight(40)).toBeGreaterThan(0);
	});
});

describe("clampHourHeight", () => {
	it("holds the height between fitting the day and the maximum", () => {
		expect(clampHourHeight(10, 25)).toBe(25);
		expect(clampHourHeight(60, 25)).toBe(60);
		expect(clampHourHeight(1000, 25)).toBe(maxHourHeight);
	});

	it("lets the fit height win on a screen so tall that 24 hours exceed the maximum", () => {
		expect(clampHourHeight(60, maxHourHeight + 20)).toBe(maxHourHeight + 20);
	});

	it("zooms by a factor and stops at both ends", () => {
		expect(zoomedHourHeight(48, 1.25, 25)).toBe(60);
		expect(zoomedHourHeight(30, 0.5, 25)).toBe(25);
		expect(zoomedHourHeight(120, 2, 25)).toBe(maxHourHeight);
	});
});

describe("anchoredScrollTop", () => {
	const viewportHeight = 500;

	// The anchor is a pixel offset into the visible grid; what must survive a zoom is the time
	// at that offset.
	function timeAt(scrollTop: number, anchorOffset: number, hourHeight: number) {
		return (scrollTop + anchorOffset) / hourHeight;
	}

	it("keeps the moment under the pointer under the pointer when zooming in", () => {
		const next = anchoredScrollTop({
			scrollTop: 400,
			anchorOffset: 150,
			oldHourHeight: 50,
			newHourHeight: 80,
			viewportHeight,
		});

		expect(timeAt(next, 150, 80)).toBeCloseTo(timeAt(400, 150, 50));
	});

	it("keeps it there when zooming out", () => {
		const next = anchoredScrollTop({
			scrollTop: 900,
			anchorOffset: 320,
			oldHourHeight: 100,
			newHourHeight: 64,
			viewportHeight,
		});

		expect(timeAt(next, 320, 64)).toBeCloseTo(timeAt(900, 320, 100));
	});

	it("anchors on the top edge as plain proportional scrolling", () => {
		expect(
			anchoredScrollTop({
				scrollTop: 240,
				anchorOffset: 0,
				oldHourHeight: 48,
				newHourHeight: 96,
				viewportHeight,
			}),
		).toBe(480);
	});

	it("never scrolls above the start of the day", () => {
		expect(
			anchoredScrollTop({
				scrollTop: 10,
				anchorOffset: 400,
				oldHourHeight: 100,
				newHourHeight: 40,
				viewportHeight,
			}),
		).toBe(0);
	});

	it("never scrolls past the end of the day when zooming out near its end", () => {
		const next = anchoredScrollTop({
			scrollTop: 1900,
			anchorOffset: 0,
			oldHourHeight: 100,
			newHourHeight: 40,
			viewportHeight,
		});

		expect(next).toBe(hoursPerDay * 40 - viewportHeight);
	});

	it("does not scroll at all once the whole day fits", () => {
		expect(
			anchoredScrollTop({
				scrollTop: 300,
				anchorOffset: 200,
				oldHourHeight: 60,
				newHourHeight: 20,
				viewportHeight: 600,
			}),
		).toBe(0);
	});
});

describe("wheelZoomFactor", () => {
	it("zooms in on an upward wheel and out on a downward one", () => {
		expect(wheelZoomFactor(-100)).toBeGreaterThan(1);
		expect(wheelZoomFactor(100)).toBeLessThan(1);
		expect(wheelZoomFactor(0)).toBe(1);
	});

	it("makes opposite deltas cancel, so a pinch in and out returns to the same scale", () => {
		expect(wheelZoomFactor(-7) * wheelZoomFactor(7)).toBeCloseTo(1);
	});

	it("bounds one huge delta and reads line-based deltas as lines", () => {
		expect(wheelZoomFactor(-100000)).toBe(2);
		expect(wheelZoomFactor(100000)).toBe(0.5);
		expect(wheelZoomFactor(-3, 1)).toBeGreaterThan(wheelZoomFactor(-3, 0));
	});
});

describe("labelStepHours", () => {
	it("labels every hour with room to spare and thins out as the scale shrinks", () => {
		expect(labelStepHours(defaultHourHeight)).toBe(1);
		expect(labelStepHours(30)).toBe(1);
		expect(labelStepHours(25)).toBe(2);
		expect(labelStepHours(14)).toBe(3);
		expect(labelStepHours(9)).toBe(4);
		expect(labelStepHours(8)).toBe(4);
	});

	it("leaves at least a label's height between printed labels at every scale", () => {
		for (let height = 8; height <= maxHourHeight; height += 1) {
			const step = labelStepHours(height);

			expect(step * height >= 30 || step === 12).toBe(true);
			expect(24 % step).toBe(0);
		}
	});
});

describe("snapMinutes", () => {
	it("snaps to half hours by default, quarter hours zoomed in, hours zoomed out", () => {
		expect(snapMinutes(defaultHourHeight)).toBe(30);
		expect(snapMinutes(96)).toBe(15);
		expect(snapMinutes(maxHourHeight)).toBe(15);
		expect(snapMinutes(35)).toBe(60);
		expect(snapMinutes(25)).toBe(60);
		expect(snapMinutes(36)).toBe(30);
	});

	it("never makes a slot shorter than a comfortable click target", () => {
		for (let height = 8; height <= maxHourHeight; height += 1) {
			expect((snapMinutes(height) / 60) * height).toBeGreaterThanOrEqual(8);
		}
	});
});

describe("minorGridlines", () => {
	it("adds half-hour then quarter-hour lines as the scale grows", () => {
		expect(minorGridlines(defaultHourHeight)).toBe("none");
		expect(minorGridlines(64)).toBe("half");
		expect(minorGridlines(111)).toBe("half");
		expect(minorGridlines(112)).toBe("quarter");
	});
});

describe("minimumBlockMinutesFor", () => {
	it("is the duration a minimum-height block spans at this scale", () => {
		expect(minimumBlockMinutesFor(48)).toBe(25);
		expect(minimumBlockMinutesFor(24)).toBe(50);
		expect(minimumBlockMinutesFor(12)).toBe(100);
	});

	it("never goes below a quarter hour however far in", () => {
		expect(minimumBlockMinutesFor(maxHourHeight)).toBe(15);
	});
});
