/** One thing to tell the user about. */
export interface AppNotification {
	id: string;
	kind: "error" | "info" | "success";
	title: string;
	detail: string;

	/** Keep passive, user-actionable failures visible until explicitly dismissed. */
	persistent?: boolean;

	/**
	 * Present when the user can do something about it.
	 *
	 * Its presence decides which Carbon component renders it: an actionable notification
	 * persists, while a passive one dismisses itself. Putting an action inside a
	 * self-dismissing toast breaks WCAG, because the control disappears while it is still
	 * reachable by keyboard (§13).
	 */
	action?: { label: string; run: () => void };
}
