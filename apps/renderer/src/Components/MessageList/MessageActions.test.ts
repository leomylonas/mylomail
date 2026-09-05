// @vitest-environment jsdom
//
// The only test file in this package that needs a DOM global (`window.confirm`) — everything
// else here runs under vitest's default node environment, which has none. jsdom is an existing
// devDependency (pulled in for vitest's own default toolchain) but otherwise unused: no
// @testing-library/react or full component-render harness exists in this repo (see passes
// 91-93/133), so this stays scoped to one file rather than switching the global environment.
import { describe, expect, it, vi } from "vitest";
import type { HubConnection } from "@microsoft/signalr";
import type { QueryClient } from "@tanstack/react-query";
import {
	messageActions,
	type MessageSummary,
} from "@mylomail/renderer/Components/MessageList/MessageList";

const message = (overrides: Partial<MessageSummary> = {}): MessageSummary => ({
	id: "m1",
	subject: "Hello",
	snippet: "",
	from: [{ name: "Someone", email: "someone@example.test" }],
	receivedAt: "2026-01-01T00:00:00.000Z",
	isRead: false,
	isFlagged: false,
	hasNonInlineAttachments: false,
	mutationFailure: null,
	...overrides,
});

function findAction(actions: ReturnType<typeof messageActions>, label: string) {
	const action = actions.find((a) => a.label.startsWith(label));
	if (!action) throw new Error(`No action starting with "${label}"`);
	return action;
}

// Pass 147: "Delete permanently" had no confirmation at all, unlike every other destructive
// action in the app (trash, mailbox delete, account removal, a recurring series' whole-series
// delete) — a single misclick on this danger-styled menu item did something the app itself
// documents as never reversible.
describe("messageActions — Delete permanently confirmation", () => {
	it("does not delete when the user cancels the confirmation", () => {
		const deletePermanently = vi.fn();
		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(false);

		const actions = messageActions(
			[message()],
			vi.fn(),
			vi.fn(),
			deletePermanently,
			vi.fn(),
			[],
			{} as HubConnection,
			{} as QueryClient,
			vi.fn(),
			vi.fn(),
			"me@example.test",
			() => () => undefined,
		);
		findAction(actions, "Delete permanently").run();

		expect(confirmSpy).toHaveBeenCalled();
		expect(deletePermanently).not.toHaveBeenCalled();
		confirmSpy.mockRestore();
	});

	it("deletes when the user confirms", () => {
		const deletePermanently = vi.fn();
		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
		const targets = [message()];

		const actions = messageActions(
			targets,
			vi.fn(),
			vi.fn(),
			deletePermanently,
			vi.fn(),
			[],
			{} as HubConnection,
			{} as QueryClient,
			vi.fn(),
			vi.fn(),
			"me@example.test",
			() => () => undefined,
		);
		findAction(actions, "Delete permanently").run();

		expect(confirmSpy).toHaveBeenCalled();
		expect(deletePermanently).toHaveBeenCalledWith(targets);
		confirmSpy.mockRestore();
	});

	it("mentions the count in the confirmation for a multi-message selection", () => {
		const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(false);

		const actions = messageActions(
			[message({ id: "m1" }), message({ id: "m2" })],
			vi.fn(),
			vi.fn(),
			vi.fn(),
			vi.fn(),
			[],
			{} as HubConnection,
			{} as QueryClient,
			vi.fn(),
			vi.fn(),
			"me@example.test",
			() => () => undefined,
		);
		findAction(actions, "Delete permanently").run();

		expect(confirmSpy).toHaveBeenCalledWith(
			expect.stringContaining("2 messages"),
		);
		confirmSpy.mockRestore();
	});
});

// §13's full-keyboard-operability requirement had no keyboard path for moving a message to an
// arbitrary folder — dragging it onto a sidebar folder was the only way. This submenu is that
// path's keyboard equivalent.
describe("messageActions — Move to", () => {
	it("offers each non-synthesized mailbox and moves the targets when one is chosen", () => {
		const moveMessages = vi.fn();
		const targets = [message({ id: "m1" }), message({ id: "m2" })];

		const actions = messageActions(
			targets,
			vi.fn(),
			vi.fn(),
			vi.fn(),
			moveMessages,
			[
				{ id: "inbox", name: "Inbox", isSynthesized: false },
				{ id: "archive", name: "Archive", isSynthesized: false },
				{ id: "gmail-group", name: "Nested", isSynthesized: true },
			],
			{} as HubConnection,
			{} as QueryClient,
			vi.fn(),
			vi.fn(),
			"me@example.test",
			() => () => undefined,
		);

		const moveTo = findAction(actions, "Move to");
		const labels = (moveTo.children ?? []).map((child) => child.label);
		expect(labels).toEqual(["Archive", "Inbox"]);

		moveTo.children?.find((child) => child.label === "Archive")?.run();
		expect(moveMessages).toHaveBeenCalledWith({
			messages: targets,
			targetMailboxId: "archive",
		});
	});

	it("is unavailable with no other folders to offer", () => {
		const actions = messageActions(
			[message()],
			vi.fn(),
			vi.fn(),
			vi.fn(),
			vi.fn(),
			[],
			{} as HubConnection,
			{} as QueryClient,
			vi.fn(),
			vi.fn(),
			"me@example.test",
			() => () => undefined,
		);

		expect(findAction(actions, "Move to").unavailable).toBeTruthy();
	});
});
