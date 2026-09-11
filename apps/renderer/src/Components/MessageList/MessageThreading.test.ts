import { describe, expect, it } from "vitest";
import {
	collapseThreads,
	expandedThreadKeysForAccount,
	messageMatchesFilter,
	sortMessages,
	type MessageSummary,
} from "@mylomail/renderer/Components/MessageList/MessageList";

function message(
	id: string,
	threadId: string | null = null,
	overrides: Partial<MessageSummary> = {},
): MessageSummary {
	return {
		id,
		threadId,
		subject: id,
		snippet: "",
		from: [{ name: "Sender", email: "sender@example.test" }],
		receivedAt: "2026-01-01T00:00:00.000Z",
		isRead: false,
		isFlagged: false,
		hasNonInlineAttachments: false,
		mutationFailure: null,
		...overrides,
	};
}

describe("collapseThreads", () => {
	it("keeps every expanded conversation member contiguous despite interleaved source rows", () => {
		const first = message("a1", "thread-a");
		const other = message("b1", "thread-b");
		const second = message("a2", "thread-a");

		const displayed = collapseThreads(
			[first, other, second],
			"collapsed",
			new Set(["thread-a"]),
		);

		expect(displayed.map((item) => item.id)).toEqual(["a1", "a2", "b1"]);
		expect(displayed[1]).toBe(second);
	});

	it("shows one representative for an unexpanded conversation and leaves unthreaded messages distinct", () => {
		const displayed = collapseThreads(
			[message("a1", "thread-a"), message("a2", "thread-a"), message("solo")],
			"collapsed",
			new Set(),
		);

		expect(displayed.map((item) => item.id)).toEqual(["a1", "solo"]);
	});

	it("sorts a conversation before collapsing it so expansion cannot split its members", () => {
		const displayed = collapseThreads(
			sortMessages(
				[
					message("a1", "thread-a", { subject: "Zulu" }),
					message("b1", "thread-b", { subject: "Middle" }),
					message("a2", "thread-a", { subject: "Alpha" }),
				],
				[{ id: "subject", desc: false }],
			),
			"collapsed",
			new Set(["thread-a"]),
		);

		expect(displayed.map((item) => item.id)).toEqual(["a2", "a1", "b1"]);
	});
	it("filters conversation members before choosing the collapsed representative", () => {
		const representative = message("newer", "thread-a", {
			subject: "Status",
		});
		const matchingMember = message("older", "thread-a", {
			subject: "Needle",
		});

		const displayed = collapseThreads(
			[representative, matchingMember].filter((item) =>
				messageMatchesFilter(item, "needle"),
			),
			"collapsed",
			new Set(),
		);

		expect(displayed.map((item) => item.id)).toEqual(["older"]);
	});

	it("scopes expanded conversation keys to their account", () => {
		const expanded = new Set(["account-a:shared-thread", "account-b:other"]);

		expect([...expandedThreadKeysForAccount(expanded, "account-b")]).toEqual([
			"other",
		]);
	});
});
