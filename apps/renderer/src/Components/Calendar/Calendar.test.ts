import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	resolveLiveModalEvent,
	type CalendarEventSummary,
} from "@mylomail/renderer/Components/Calendar/Calendar";

function event(
	overrides: Partial<CalendarEventSummary> = {},
): CalendarEventSummary {
	return {
		id: "event-1",
		calendarId: "cal-1",
		title: "Original title",
		location: null,
		description: null,
		start: "2026-09-06T09:00:00.000Z",
		end: "2026-09-06T10:00:00.000Z",
		isAllDay: false,
		isRecurring: false,
		syncConflict: false,
		isVirtualOccurrence: false,
		masterEventId: null,
		isRecurrenceMaster: false,
		...overrides,
	};
}

describe("resolveLiveModalEvent", () => {
	it("returns undefined when no modal is open", () => {
		expect(resolveLiveModalEvent(null, new Map())).toBeUndefined();
	});

	it("returns undefined for a create-mode modal", () => {
		const modal = {
			mode: "create" as const,
			calendarId: "cal-1",
			date: dayjs(),
		};
		expect(resolveLiveModalEvent(modal, new Map())).toBeUndefined();
	});

	// The core fix: a background sync flagging SyncConflict while the modal stays open must be
	// reflected, not the value frozen at the moment the modal was opened.
	it("prefers the live event over the frozen snapshot when both exist", () => {
		const snapshot = event({ syncConflict: false });
		const live = event({ syncConflict: true });
		const modal = { mode: "edit" as const, event: snapshot };
		const eventsById = new Map([[live.id, live]]);
		expect(resolveLiveModalEvent(modal, eventsById)?.syncConflict).toBe(true);
	});

	// If the event has dropped out of the current query window (e.g. its own edit rescheduled
	// it outside the visible month), the modal must keep showing something rather than nothing.
	it("falls back to the frozen snapshot when the event isn't in the live map", () => {
		const snapshot = event({ syncConflict: true });
		const modal = { mode: "edit" as const, event: snapshot };
		expect(resolveLiveModalEvent(modal, new Map())).toBe(snapshot);
	});
});
