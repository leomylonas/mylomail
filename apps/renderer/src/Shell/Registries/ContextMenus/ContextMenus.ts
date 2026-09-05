/** One entry in a context menu. */
export interface MenuAction {
	label: string;
	run: () => void;

	/**
	 * Why the entry cannot be used, when it cannot.
	 *
	 * Present rather than absent, because §13 asks for the conventional menu for each item
	 * type: an entry that vanishes when unavailable teaches the user the app is inconsistent,
	 * whereas one that is visibly disabled tells them the feature exists and why it is not
	 * available now.
	 */
	unavailable?: string;

	danger?: boolean;

	/**
	 * A nested submenu, when present, instead of `run` — Carbon's `Menu`/`MenuItem` render this
	 * as a keyboard-navigable flyout with no extra positioning of its own to manage. Used for
	 * "Move to <folder>" (§13's full-keyboard-operability requirement: dragging a message onto
	 * a sidebar folder was previously the only way to move it anywhere other than trash).
	 */
	children?: readonly MenuAction[];
}
