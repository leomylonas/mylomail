import { describe, expect, it, vi } from "vitest";
import { surfaceStartupFailure } from "@mylomail/electron-shell/StartupFailure";

describe("surfaceStartupFailure", () => {
	it("shows migration details in a native fatal-error dialog", () => {
		const showErrorBox = vi.fn();

		surfaceStartupFailure(
			{ showErrorBox },
			new Error("Migration 20260814_AddAccounts could not be applied."),
		);

		expect(showErrorBox).toHaveBeenCalledOnce();
		expect(showErrorBox).toHaveBeenCalledWith(
			"MyloMail could not start",
			expect.stringMatching(
				/database upgrade fails[\s\S]*Migration 20260814_AddAccounts could not be applied/,
			),
		);
	});

	it("does not let unbounded backend output overwhelm the dialog", () => {
		const showErrorBox = vi.fn();

		surfaceStartupFailure({ showErrorBox }, "x".repeat(10_000));

		const content = showErrorBox.mock.calls[0]?.[1] as string;
		expect(content.length).toBeLessThan(4_500);
	});
});
