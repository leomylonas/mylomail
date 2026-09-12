import { describe, expect, it, vi } from "vitest";
import {
	describeMailboxCount,
	mailboxMoveActions,
	type Mailbox,
} from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { MailboxAvailability } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

function mailbox(overrides: Partial<Mailbox> & { id: string }): Mailbox {
	return {
		parentId: null,
		name: overrides.id,
		providerTotalCount: null,
		providerUnreadCount: null,
		localCount: 0,
		isCollapsed: false,
		availability: MailboxAvailability.Usable,
		coverage: 0,
		initialSyncModeOverride: null,
		initialSyncBoundValueOverride: null,
		isSynthesized: false,
		specialUse: 0,
		specialUseOverride: null,
		...overrides,
	};
}

describe("mailbox sidebar counts", () => {
	it("shows zero unread and the provider total together", () => {
		expect(
			describeMailboxCount(
				mailbox({
					id: "inbox",
					providerUnreadCount: 0,
					providerTotalCount: 42,
					localCount: 5,
				}),
			),
		).toBe("0 unread · 42 total");
	});

	it("labels the local fallback as held when the provider total is unavailable", () => {
		expect(
			describeMailboxCount(
				mailbox({
					id: "archive",
					providerUnreadCount: 3,
					providerTotalCount: null,
					localCount: 8,
				}),
			),
		).toBe("3 unread · 8 held");
	});

	it("still reports the provider total when unread count is unavailable", () => {
		expect(
			describeMailboxCount(
				mailbox({
					id: "sent",
					providerUnreadCount: null,
					providerTotalCount: 9,
				}),
			),
		).toBe("9 total");
	});
});

function findAction(
	actions: ReturnType<typeof mailboxMoveActions>,
	label: string,
) {
	const action = actions.find((a) => a.label === label);
	if (!action) throw new Error(`No "${label}" action`);
	return action;
}

// §13's full-keyboard-operability requirement had no keyboard path for reordering a folder
// among its siblings or reparenting it — dragging it onto a sibling or a different parent was
// the only way. This menu is that path's keyboard equivalent, mirroring pass 201's identical
// fix for moving a message.
describe("mailboxMoveActions — Move up/down", () => {
	it("swaps with the previous or next sibling, preserving the others", () => {
		const siblings = [
			mailbox({ id: "a" }),
			mailbox({ id: "b" }),
			mailbox({ id: "c" }),
		];
		const reorder = vi.fn();

		findAction(
			mailboxMoveActions(siblings, siblings[1], reorder, vi.fn()),
			"Move up",
		).run();
		expect(reorder).toHaveBeenCalledWith(["b", "a", "c"]);

		reorder.mockClear();
		findAction(
			mailboxMoveActions(siblings, siblings[1], reorder, vi.fn()),
			"Move down",
		).run();
		expect(reorder).toHaveBeenCalledWith(["a", "c", "b"]);
	});

	it("disables Move up on the first sibling and Move down on the last", () => {
		const siblings = [mailbox({ id: "a" }), mailbox({ id: "b" })];

		expect(
			findAction(
				mailboxMoveActions(siblings, siblings[0], vi.fn(), vi.fn()),
				"Move up",
			).unavailable,
		).toBeTruthy();
		expect(
			findAction(
				mailboxMoveActions(siblings, siblings[0], vi.fn(), vi.fn()),
				"Move down",
			).unavailable,
		).toBeFalsy();

		expect(
			findAction(
				mailboxMoveActions(siblings, siblings[1], vi.fn(), vi.fn()),
				"Move down",
			).unavailable,
		).toBeTruthy();
	});

	it("only swaps within the same parent, ignoring mailboxes under a different one", () => {
		const all = [
			mailbox({ id: "a", parentId: "root" }),
			mailbox({ id: "other", parentId: "somewhere-else" }),
			mailbox({ id: "b", parentId: "root" }),
		];
		const reorder = vi.fn();

		findAction(
			mailboxMoveActions(all, all[2], reorder, vi.fn()),
			"Move up",
		).run();
		expect(reorder).toHaveBeenCalledWith(["b", "a"]);
	});
});

describe("mailboxMoveActions — Move to", () => {
	it("offers every other mailbox sorted by name, and reparents when one is chosen", () => {
		const mine = mailbox({ id: "mine", parentId: "root", name: "Mine" });
		const all = [
			mine,
			mailbox({ id: "root", name: "Root" }),
			mailbox({ id: "zeta", name: "Zeta" }),
			mailbox({ id: "alpha", name: "Alpha" }),
		];
		const reparent = vi.fn();

		const moveTo = findAction(
			mailboxMoveActions(all, mine, vi.fn(), reparent),
			"Move to",
		);
		// "root" is mine's own current parent — a no-op MoveMailbox call — so it's excluded.
		expect((moveTo.children ?? []).map((c) => c.label)).toEqual([
			"Alpha",
			"Zeta",
		]);

		moveTo.children?.find((c) => c.label === "Alpha")?.run();
		expect(reparent).toHaveBeenCalledWith("alpha");
	});

	it("excludes the mailbox's own descendants, since that would cycle ParentId", () => {
		const parent = mailbox({ id: "parent" });
		const child = mailbox({ id: "child", parentId: "parent" });
		const grandchild = mailbox({ id: "grandchild", parentId: "child" });
		const other = mailbox({ id: "other" });

		const labels = (
			findAction(
				mailboxMoveActions(
					[parent, child, grandchild, other],
					parent,
					vi.fn(),
					vi.fn(),
				),
				"Move to",
			).children ?? []
		).map((c) => c.label);

		expect(labels).toEqual(["other"]);
	});

	it("is unavailable with no other folders to offer", () => {
		const only = mailbox({ id: "only" });
		expect(
			findAction(mailboxMoveActions([only], only, vi.fn(), vi.fn()), "Move to")
				.unavailable,
		).toBeTruthy();
	});
});

// A synthesized row (a Gmail nested-label-group intermediate the sidebar derives, with no real
// label of its own) has nothing for MoveMailboxAsync's provider PATCH to act on — reparenting
// one would surface RunProviderCallAsync's internal assertion message ("Gmail mailboxes always
// have a provider id") instead of the same clear guidance Rename/Delete already give for this
// exact row shape.
describe("mailboxMoveActions — synthesized rows can't be reparented", () => {
	it("disables Move to and Move to top level, but not Move up/down", () => {
		const synthesized = mailbox({
			id: "work",
			name: "Work",
			parentId: "root",
			isSynthesized: true,
		});
		const all = [
			mailbox({ id: "root", name: "Root" }),
			synthesized,
			mailbox({ id: "other", name: "Other", parentId: "root" }),
		];

		const actions = mailboxMoveActions(all, synthesized, vi.fn(), vi.fn());

		expect(findAction(actions, "Move to").unavailable).toBe(
			"Gmail doesn't support moving a nested label group directly — move the label itself in Gmail.",
		);
		expect(findAction(actions, "Move to top level").unavailable).toBe(
			"Gmail doesn't support moving a nested label group directly — move the label itself in Gmail.",
		);
		// A same-parent reorder never touches the provider (LocalSortOrder only), so it stays
		// available even for a synthesized row.
		expect(findAction(actions, "Move down").unavailable).toBeFalsy();
	});
});

describe("mailboxMoveActions — Move to top level", () => {
	it("reparents to null and is unavailable when already at the top level", () => {
		const nested = mailbox({ id: "nested", parentId: "root" });
		const reparent = vi.fn();

		const action = findAction(
			mailboxMoveActions([nested], nested, vi.fn(), reparent),
			"Move to top level",
		);
		expect(action.unavailable).toBeFalsy();
		action.run();
		expect(reparent).toHaveBeenCalledWith(null);

		const topLevel = mailbox({ id: "top", parentId: null });
		expect(
			findAction(
				mailboxMoveActions([topLevel], topLevel, vi.fn(), vi.fn()),
				"Move to top level",
			).unavailable,
		).toBeTruthy();
	});
});
