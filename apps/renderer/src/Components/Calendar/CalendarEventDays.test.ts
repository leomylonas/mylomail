import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { eventOverlapsDay } from "@mylomail/renderer/Components/Calendar/CalendarEventDays";

function timed(start: string, end: string) {
	return {
		start: dayjs(start).toISOString(),
		end: dayjs(end).toISOString(),
		isAllDay: false,
	};
}

describe("eventOverlapsDay", () => {
	it("places a timed event on each local day it touches", () => {
		const overnight = timed("2026-10-05T22:00", "2026-10-06T02:00");

		expect(eventOverlapsDay(overnight, dayjs("2026-10-04"))).toBe(false);
		expect(eventOverlapsDay(overnight, dayjs("2026-10-05"))).toBe(true);
		expect(eventOverlapsDay(overnight, dayjs("2026-10-06"))).toBe(true);
		expect(eventOverlapsDay(overnight, dayjs("2026-10-07"))).toBe(false);
	});

	it("does not spill an event ending at midnight into the next day", () => {
		const evening = timed("2026-10-05T20:00", "2026-10-06T00:00");

		expect(eventOverlapsDay(evening, dayjs("2026-10-05"))).toBe(true);
		expect(eventOverlapsDay(evening, dayjs("2026-10-06"))).toBe(false);
	});

	// All-day dates are literal calendar dates stored as UTC midnight with an exclusive end;
	// reading them as instants would shift them by the viewer's UTC offset.
	it("keeps a one-day all-day event on exactly its own date in any time zone", () => {
		const holiday = {
			start: "2026-10-05T00:00:00.000Z",
			end: "2026-10-06T00:00:00.000Z",
			isAllDay: true,
		};

		expect(eventOverlapsDay(holiday, dayjs("2026-10-04"))).toBe(false);
		expect(eventOverlapsDay(holiday, dayjs("2026-10-05"))).toBe(true);
		expect(eventOverlapsDay(holiday, dayjs("2026-10-06"))).toBe(false);
	});

	it("covers every date of a multi-day all-day event, end exclusive", () => {
		const trip = {
			start: "2026-10-05T00:00:00.000Z",
			end: "2026-10-08T00:00:00.000Z",
			isAllDay: true,
		};

		const covered = [4, 5, 6, 7, 8].map((date) =>
			eventOverlapsDay(trip, dayjs(`2026-10-0${date}`)),
		);
		expect(covered).toEqual([false, true, true, true, false]);
	});

	it("still shows an all-day event whose end was stored equal to its start", () => {
		const degenerate = {
			start: "2026-10-05T00:00:00.000Z",
			end: "2026-10-05T00:00:00.000Z",
			isAllDay: true,
		};

		expect(eventOverlapsDay(degenerate, dayjs("2026-10-05"))).toBe(true);
		expect(eventOverlapsDay(degenerate, dayjs("2026-10-06"))).toBe(false);
	});
});
