import { describe, expect, it } from "vitest";
import {
	calendarsInView,
	seedHiddenState,
} from "@mylomail/renderer/Components/Calendar/CalendarVisibility";

function calendar(id: string, isHidden = false) {
	return { id, isHidden };
}

describe("calendarsInView", () => {
	it("shows every calendar when none is hidden", () => {
		const calendars = [calendar("a"), calendar("b")];

		expect(calendarsInView(calendars, {})).toEqual(calendars);
	});

	it("leaves out a calendar the server reported as hidden", () => {
		const calendars = [calendar("a"), calendar("b", true)];

		expect(calendarsInView(calendars, {}).map((c) => c.id)).toEqual(["a"]);
	});

	it("lets this window's own choice override the persisted default", () => {
		const calendars = [calendar("a"), calendar("b", true)];

		expect(
			calendarsInView(calendars, { a: true, b: false }).map((c) => c.id),
		).toEqual(["b"]);
	});

	it("shows only the requested calendar in a window opened for one calendar", () => {
		const calendars = [calendar("a"), calendar("b"), calendar("c", true)];

		expect(calendarsInView(calendars, {}, "b").map((c) => c.id)).toEqual(["b"]);
		// Hidden in the unified view, but the window was opened for it on purpose.
		expect(
			calendarsInView(calendars, { c: true }, "c").map((c) => c.id),
		).toEqual(["c"]);
	});

	it("shows nothing when the requested calendar no longer exists", () => {
		expect(calendarsInView([calendar("a")], {}, "gone")).toEqual([]);
	});
});

describe("seedHiddenState", () => {
	it("records the persisted default of calendars not seen before", () => {
		expect(seedHiddenState({}, [calendar("a"), calendar("b", true)])).toEqual({
			a: false,
			b: true,
		});
	});

	// Another window toggling a calendar changes the server value; a refetch here must not
	// drag this window along with it.
	it("keeps a calendar's own value when the server's default changes later", () => {
		const seeded = seedHiddenState({}, [calendar("a")]);
		const refetched = seedHiddenState({ ...seeded, a: true }, [
			calendar("a", false),
			calendar("b", true),
		]);

		expect(refetched).toEqual({ a: true, b: true });
	});

	it("returns the same object when there is nothing to seed", () => {
		const live = { a: false };

		expect(seedHiddenState(live, [calendar("a", true)])).toBe(live);
	});
});
