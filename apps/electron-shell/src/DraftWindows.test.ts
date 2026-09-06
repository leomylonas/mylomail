import { describe, expect, it } from "vitest";
import { windowAlreadyEditing } from "@mylomail/electron-shell/DraftWindows";

describe("windowAlreadyEditing", () => {
	it("finds another window already editing the same draft", () => {
		const draftWindows = new Map([
			[1, "draft-a"],
			[2, "draft-b"],
		]);

		expect(windowAlreadyEditing(draftWindows, 3, "draft-b")).toBe(2);
	});

	it("ignores the requester's own window even if it reported the same draft", () => {
		const draftWindows = new Map([[1, "draft-a"]]);

		expect(windowAlreadyEditing(draftWindows, 1, "draft-a")).toBeUndefined();
	});

	it("returns undefined when no other window has that draft open", () => {
		const draftWindows = new Map([
			[1, "draft-a"],
			[2, null],
		]);

		expect(windowAlreadyEditing(draftWindows, 1, "draft-c")).toBeUndefined();
	});
});
