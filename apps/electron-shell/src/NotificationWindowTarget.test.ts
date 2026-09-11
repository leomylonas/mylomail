import { describe, expect, it } from "vitest";
import { notificationTargetWindow } from "@mylomail/electron-shell/NotificationWindowTarget";

describe("notificationTargetWindow", () => {
	it("uses the focused main window without changing the other independent window", () => {
		expect(
			notificationTargetWindow(new Set([1, 2]), 2, (id) => `window-${id}`),
		).toBe("window-2");
	});

	it("ignores a focused auxiliary window and falls back to an available main window", () => {
		expect(
			notificationTargetWindow(new Set([1, 2]), 9, (id) => `window-${id}`),
		).toBe("window-1");
	});

	it("returns no target when every tracked main window is gone", () => {
		expect(
			notificationTargetWindow(new Set([1, 2]), 1, () => undefined),
		).toBeUndefined();
	});
});
