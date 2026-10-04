import { describe, expect, it } from "vitest";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { calendarGridStart } from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import { agendaDayIndex } from "@mylomail/renderer/Components/Calendar/CalendarAgenda/CalendarAgendaFocus";

const agendaDays = 42;

describe("agendaDayIndex", () => {
	it("counts days from the start of the range", () => {
		const start = dayjs("2026-09-27");

		expect(agendaDayIndex(start, dayjs("2026-09-27T18:30"), agendaDays)).toBe(
			0,
		);
		expect(agendaDayIndex(start, dayjs("2026-10-04"), agendaDays)).toBe(7);
		expect(agendaDayIndex(start, dayjs("2026-11-07"), agendaDays)).toBe(41);
	});

	it("is -1 for a day outside the range", () => {
		const start = dayjs("2026-09-27");

		expect(agendaDayIndex(start, dayjs("2026-09-26"), agendaDays)).toBe(-1);
		expect(agendaDayIndex(start, dayjs("2026-11-08"), agendaDays)).toBe(-1);
	});

	// Choosing a day number in the month grid makes that day the header's anchor. The agenda
	// range is the six weeks around the anchor, so the chosen day — including one of the greyed
	// days from the neighbouring month — must always be a row the agenda can scroll to.
	it("finds every day of the month grid when that day becomes the anchor", () => {
		const shownMonth = dayjs("2026-10-15");
		const gridStart = calendarGridStart(shownMonth, 0);

		for (let offset = 0; offset < agendaDays; offset += 1) {
			const chosen = gridStart.add(offset, "day");
			const rangeStart = calendarGridStart(chosen, 0);

			expect(
				agendaDayIndex(rangeStart, chosen, agendaDays),
			).toBeGreaterThanOrEqual(0);
		}
	});
});
