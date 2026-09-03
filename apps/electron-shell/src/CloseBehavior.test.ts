import { describe, expect, it } from "vitest";
import { closeBehaviorFromValue } from "@mylomail/electron-shell/CloseBehavior";

describe("closeBehaviorFromValue", () => {
	it("maps the CloseBehavior.MinimizeToTray ordinal to MinimizeToTray", () => {
		expect(closeBehaviorFromValue(1)).toBe("MinimizeToTray");
	});

	it("maps every other value, including the QuitApp ordinal, to QuitApp", () => {
		expect(closeBehaviorFromValue(0)).toBe("QuitApp");
		expect(closeBehaviorFromValue(undefined)).toBe("QuitApp");
		expect(closeBehaviorFromValue("MinimizeToTray")).toBe("QuitApp");
	});
});
