import { describe, expect, it, vi } from "vitest";
import {
	resolve,
	type Shortcut,
} from "@mylomail/renderer/Shell/Registries/Shortcuts/Shortcuts";

const shortcut = (overrides: Partial<Shortcut>): Shortcut => ({
	key: "u",
	description: "Mark unread",
	run: vi.fn(),
	...overrides,
});

const event = (init: Partial<KeyboardEvent> & { key: string }): KeyboardEvent =>
	({
		ctrlKey: false,
		metaKey: false,
		shiftKey: false,
		target: null,
		...init,
	}) as KeyboardEvent;

describe("shortcut resolution", () => {
	it("matches a bare key", () => {
		const binding = shortcut({ key: "u" });

		expect(resolve([binding], event({ key: "u" }))).toBe(binding);
		expect(resolve([binding], event({ key: "U" }))).toBe(binding);
	});

	it("does not match when a modifier is held that the binding does not want", () => {
		const binding = shortcut({ key: "u" });

		expect(
			resolve([binding], event({ key: "u", ctrlKey: true })),
		).toBeUndefined();
	});

	it("matches ctrl or meta interchangeably, so one binding serves every platform", () => {
		const binding = shortcut({ key: "f", ctrlOrMeta: true });

		expect(resolve([binding], event({ key: "f", ctrlKey: true }))).toBe(
			binding,
		);
		expect(resolve([binding], event({ key: "f", metaKey: true }))).toBe(
			binding,
		);
	});

	/**
	 * Gmail and Outlook bind bare letters, so a registry that fired them inside a text field
	 * would make typing impossible — pressing "u" in the search box would mark mail unread.
	 */
	it("ignores bare keys while the user is typing", () => {
		const binding = shortcut({ key: "u" });
		const input = {
			tagName: "INPUT",
			isContentEditable: false,
		} as unknown as EventTarget;

		expect(
			resolve([binding], event({ key: "u", target: input })),
		).toBeUndefined();
	});

	/** A modified combination inside a field is the field's business, and still resolves. */
	it("still matches modified keys while typing", () => {
		const binding = shortcut({ key: "f", ctrlOrMeta: true });
		const input = {
			tagName: "INPUT",
			isContentEditable: false,
		} as unknown as EventTarget;

		expect(
			resolve([binding], event({ key: "f", ctrlKey: true, target: input })),
		).toBe(binding);
	});
});
