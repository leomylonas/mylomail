import { describe, expect, it } from "vitest";
import {
	inboxUnreadCount,
	movePinnedBefore,
	pinnedMailboxes,
	systemRank,
} from "@mylomail/renderer/Components/MailboxTree/MailboxOrdering";
import type { Mailbox } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import {
	MailboxAvailability,
	SpecialUse,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

function mailbox(overrides: Partial<Mailbox> & { id: string }): Mailbox {
	return {
		parentId: null,
		name: overrides.id,
		providerTotalCount: null,
		unreadCount: 0,
		localCount: 0,
		isCollapsed: false,
		isPinned: false,
		pinnedSortOrder: 0,
		availability: MailboxAvailability.Usable,
		coverage: 0,
		coverageMessagesFetched: 0,
		coverageEstimatedTotal: null,
		initialSyncModeOverride: null,
		initialSyncBoundValueOverride: null,
		isSynthesized: false,
		specialUse: SpecialUse.None,
		specialUseOverride: null,
		...overrides,
	};
}

const accounts = [
	{ id: "a", displayName: "Work" },
	{ id: "b", displayName: "Home" },
];

describe("inboxUnreadCount", () => {
	it("counts the inbox by effective role, ignoring other folders", () => {
		expect(
			inboxUnreadCount([
				mailbox({
					id: "x",
					name: "Mail",
					specialUseOverride: SpecialUse.Inbox,
					unreadCount: 4,
				}),
				mailbox({
					id: "y",
					specialUse: SpecialUse.Trash,
					unreadCount: 33,
				}),
			]),
		).toBe(4);
	});
});

describe("pinnedMailboxes", () => {
	it("qualifies only names that collide across accounts", () => {
		const result = pinnedMailboxes(
			accounts,
			new Map([
				[
					"a",
					[
						mailbox({
							id: "a1",
							name: "INBOX",
							specialUse: SpecialUse.Inbox,
							isPinned: true,
						}),
						mailbox({ id: "a2", name: "Bills", isPinned: true }),
					],
				],
				[
					"b",
					[
						mailbox({
							id: "b1",
							name: "Inbox",
							specialUse: SpecialUse.Inbox,
							isPinned: true,
						}),
						mailbox({ id: "b2", name: "Other" }),
					],
				],
			]),
		);
		expect(result.map((p) => [p.label, p.accountQualifier])).toEqual([
			["Inbox", "Work"],
			["Bills", null],
			["Inbox", "Home"],
		]);
	});

	it("puts system folders ahead of user folders within an account", () => {
		expect(
			systemRank(mailbox({ id: "s", specialUse: SpecialUse.Sent })),
		).toBeLessThan(systemRank(mailbox({ id: "u" })));
	});
});

describe("movePinnedBefore", () => {
	it("drops the dragged folder immediately before the target, in either direction", () => {
		expect(movePinnedBefore(["a", "b", "c"], "c", "a")).toEqual([
			"c",
			"a",
			"b",
		]);
		expect(movePinnedBefore(["a", "b", "c"], "a", "c")).toEqual([
			"b",
			"a",
			"c",
		]);
	});

	it("reports no change for a drop that leaves the order as it was", () => {
		expect(movePinnedBefore(["a", "b", "c"], "a", "b")).toBeNull();
		expect(movePinnedBefore(["a", "b"], "a", "a")).toBeNull();
		expect(movePinnedBefore(["a", "b"], "x", "a")).toBeNull();
	});
});

describe("pinned order", () => {
	it("follows the user's order across accounts, not account order", () => {
		const result = pinnedMailboxes(
			accounts,
			new Map([
				["a", [mailbox({ id: "a1", isPinned: true, pinnedSortOrder: 1 })]],
				["b", [mailbox({ id: "b1", isPinned: true, pinnedSortOrder: 0 })]],
			]),
		);
		expect(result.map((p) => p.mailbox.id)).toEqual(["b1", "a1"]);
	});
});
