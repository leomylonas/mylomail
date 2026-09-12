import { describe, expect, it, vi } from "vitest";
import { printWebContents } from "@mylomail/electron-shell/Print";

describe("printWebContents", () => {
	it("prints the current document with message backgrounds", async () => {
		const print = vi.fn(
			(
				_options: { printBackground: boolean },
				callback: (success: boolean, failureReason: string) => void,
			) => callback(true, ""),
		);

		await printWebContents({ print });

		expect(print).toHaveBeenCalledWith(
			{ printBackground: true },
			expect.any(Function),
		);
	});

	it("treats closing the native print dialog as cancellation", async () => {
		const print = vi.fn(
			(
				_options: { printBackground: boolean },
				callback: (success: boolean, failureReason: string) => void,
			) => callback(false, "Print job canceled"),
		);

		await expect(printWebContents({ print })).resolves.toBe(false);
	});

	it("rejects when Electron reports that printing failed", async () => {
		const print = vi.fn(
			(
				_options: { printBackground: boolean },
				callback: (success: boolean, failureReason: string) => void,
			) => callback(false, "No printer available"),
		);

		await expect(printWebContents({ print })).rejects.toThrow(
			"No printer available",
		);
	});
});
