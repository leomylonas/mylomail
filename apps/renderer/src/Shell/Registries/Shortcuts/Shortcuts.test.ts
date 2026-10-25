import { describe, expect, it } from "vitest";
import {
	resolve,
	type Shortcut,
} from "@mylomail/renderer/Shell/Registries/Shortcuts/Shortcuts";

const selectAll: Shortcut = {
	key: "a",
	ctrlOrMeta: true,
	unlessTyping: true,
	description: "Select all",
	run: () => undefined,
};

const press = (tagName: string): KeyboardEvent =>
	({
		key: "a",
		ctrlKey: true,
		metaKey: false,
		shiftKey: false,
		target: { tagName },
	}) as unknown as KeyboardEvent;

describe("a shortcut that leaves modified keys to text fields", () => {
	it("fires from the page", () => {
		expect(resolve([selectAll], press("DIV"))).toBe(selectAll);
	});

	it("does not fire while a text field has the focus, so Ctrl+A still selects its text", () => {
		expect(resolve([selectAll], press("INPUT"))).toBeUndefined();
		expect(resolve([selectAll], press("TEXTAREA"))).toBeUndefined();
	});
});
