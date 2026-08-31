/** One keyboard binding. */
export interface Shortcut {
	/** The `KeyboardEvent.key`, compared case-insensitively. */
	key: string;
	ctrlOrMeta?: boolean;
	shift?: boolean;
	description: string;
	run: () => void;
}

/**
 * Whether a keystroke should be treated as a shortcut at all.
 *
 * <b>Not while the user is typing.</b> Gmail and Outlook both bind bare letters — `r` to
 * reply, `u` to mark unread — and a registry that fired those inside the search box would make
 * text entry impossible. Modified combinations still fire, because Ctrl+A in a text field is
 * the field's business and browsers already handle it.
 */
export function isTypingTarget(target: EventTarget | null): boolean {
	// Checked structurally rather than with `instanceof HTMLElement`, so this is testable
	// outside a DOM. The shape is what matters here, not the prototype chain.
	const element = target as {
		tagName?: string;
		isContentEditable?: boolean;
	} | null;
	if (!element?.tagName) return false;

	return (
		element.isContentEditable === true ||
		["input", "textarea", "select"].includes(element.tagName.toLowerCase())
	);
}

/** Whether an event matches a binding. */
export function matches(shortcut: Shortcut, event: KeyboardEvent): boolean {
	const modifier = event.ctrlKey || event.metaKey;

	return (
		event.key.toLowerCase() === shortcut.key.toLowerCase() &&
		modifier === Boolean(shortcut.ctrlOrMeta) &&
		event.shiftKey === Boolean(shortcut.shift)
	);
}

/** The first binding an event matches, or nothing. */
export function resolve(
	shortcuts: readonly Shortcut[],
	event: KeyboardEvent,
): Shortcut | undefined {
	// Bare-letter bindings are suppressed while typing; modified ones are not.
	const bare = !event.ctrlKey && !event.metaKey;
	if (bare && isTypingTarget(event.target)) return undefined;

	return shortcuts.find((shortcut) => matches(shortcut, event));
}
