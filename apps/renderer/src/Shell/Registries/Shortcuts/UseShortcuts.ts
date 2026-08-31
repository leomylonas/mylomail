import { useEffect } from "react";
import {
	resolve,
	type Shortcut,
} from "@mylomail/renderer/Shell/Registries/Shortcuts/Shortcuts";

/**
 * Binds this window's keyboard shortcuts.
 *
 * Registered per window, on the window's own document: a background window must not act on a
 * keystroke the user aimed at the one in front of them, which a module-level listener could
 * not distinguish (§12).
 */
export function useShortcuts(shortcuts: readonly Shortcut[]): void {
	useEffect(() => {
		const onKeyDown = (event: KeyboardEvent) => {
			const shortcut = resolve(shortcuts, event);
			if (!shortcut) return;

			// Only once it is going to act: swallowing a key the registry ignores would break
			// browser and Carbon behaviour the user expects.
			event.preventDefault();
			shortcut.run();
		};

		document.addEventListener("keydown", onKeyDown);
		return () => document.removeEventListener("keydown", onKeyDown);
	}, [shortcuts]);
}
