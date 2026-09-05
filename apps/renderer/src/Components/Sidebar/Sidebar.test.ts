import { describe, expect, it, vi } from "vitest";
import {
	accountMoveActions,
	authStateWarning,
} from "@mylomail/renderer/Components/Sidebar/Sidebar";
import { AuthState } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

function findAction(
	actions: ReturnType<typeof accountMoveActions>,
	label: string,
) {
	const action = actions.find((a) => a.label === label);
	if (!action) throw new Error(`No "${label}" action`);
	return action;
}

function account(id: string) {
	return { id, displayName: id, color: "#000000" };
}

describe("authStateWarning", () => {
	it("describes a reauthentication requirement", () => {
		expect(authStateWarning(AuthState.NeedsReauth)).toMatch(/reauthenticated/);
	});

	it("describes a locked credential store distinctly from a reauth prompt", () => {
		const message = authStateWarning(AuthState.CredentialStoreUnavailable);
		expect(message).toMatch(/keychain/);
		expect(message).not.toMatch(/reauthenticated/);
	});

	it("describes a generic connection error", () => {
		expect(authStateWarning(AuthState.Error)).toMatch(/connection problem/);
	});
});

describe("accountMoveActions — Move up/down", () => {
	const accounts = [account("a"), account("b"), account("c")];

	it("swaps with the previous account on Move up", () => {
		const reorder = vi.fn();
		findAction(
			accountMoveActions(accounts, accounts[1], reorder),
			"Move up",
		).run();
		expect(reorder).toHaveBeenCalledWith(["b", "a", "c"]);
	});

	it("swaps with the next account on Move down", () => {
		const reorder = vi.fn();
		findAction(
			accountMoveActions(accounts, accounts[1], reorder),
			"Move down",
		).run();
		expect(reorder).toHaveBeenCalledWith(["a", "c", "b"]);
	});

	it("disables Move up for the first account", () => {
		expect(
			findAction(accountMoveActions(accounts, accounts[0], vi.fn()), "Move up")
				.unavailable,
		).toBeDefined();
	});

	it("disables Move down for the last account", () => {
		expect(
			findAction(
				accountMoveActions(accounts, accounts[2], vi.fn()),
				"Move down",
			).unavailable,
		).toBeDefined();
	});

	it("leaves the middle account's Move up/down both enabled", () => {
		const actions = accountMoveActions(accounts, accounts[1], vi.fn());
		expect(findAction(actions, "Move up").unavailable).toBeUndefined();
		expect(findAction(actions, "Move down").unavailable).toBeUndefined();
	});
});
