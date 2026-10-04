/**
 * Which calendars the unified view shows (§13 Epic 7).
 *
 * The server keeps one persisted default per calendar (`isHidden`), which a window reads once
 * when it opens. From then on the window owns its own choice in `LiveHidden`: ticking a
 * calendar here must not move another open window, and another window's change must not move
 * this one, so a later refetch of the calendar list never overwrites what was already seeded.
 */

/** Calendar id to hidden, for the calendars this window has already seeded or toggled. */
export type LiveHidden = Readonly<Record<string, boolean>>;

interface VisibilityCalendar {
	id: string;
	/** The persisted default the server reported when this window first saw the calendar. */
	isHidden: boolean;
}

export function isCalendarHidden(
	calendar: VisibilityCalendar,
	live: LiveHidden,
): boolean {
	return live[calendar.id] ?? calendar.isHidden;
}

/**
 * The calendars whose events feed the month grid and the agenda. A window opened for one
 * calendar shows exactly that calendar, regardless of what the unified view hides.
 */
export function calendarsInView<T extends VisibilityCalendar>(
	calendars: readonly T[],
	live: LiveHidden,
	onlyCalendarId?: string,
): T[] {
	if (onlyCalendarId !== undefined) {
		return calendars.filter((calendar) => calendar.id === onlyCalendarId);
	}
	return calendars.filter((calendar) => !isCalendarHidden(calendar, live));
}

/**
 * Records the persisted default of every calendar this window has not seen yet. Calendars
 * already in `live` keep this window's own value; the same object comes back when there is
 * nothing to add, so it can be compared cheaply.
 */
export function seedHiddenState(
	live: LiveHidden,
	calendars: readonly VisibilityCalendar[],
): LiveHidden {
	const unseen = calendars.filter((calendar) => !(calendar.id in live));
	if (unseen.length === 0) return live;

	return {
		...live,
		...Object.fromEntries(
			unseen.map((calendar) => [calendar.id, calendar.isHidden]),
		),
	};
}
