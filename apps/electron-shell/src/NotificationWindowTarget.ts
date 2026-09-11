/**
 * Chooses the one main window a native-notification click should navigate. Auxiliary message and
 * compose windows are deliberately excluded: selection is per-window state, and broadcasting the
 * click would overwrite every independent main window at once.
 */
export function notificationTargetWindow<T>(
	mainWindowIds: ReadonlySet<number>,
	focusedWindowId: number | undefined,
	resolve: (windowId: number) => T | undefined,
): T | undefined {
	if (focusedWindowId !== undefined && mainWindowIds.has(focusedWindowId)) {
		const focused = resolve(focusedWindowId);
		if (focused !== undefined) return focused;
	}

	for (const id of mainWindowIds) {
		if (id === focusedWindowId) continue;
		const window = resolve(id);
		if (window !== undefined) return window;
	}
	return undefined;
}
