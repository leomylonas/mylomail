import { describe, expect, it, vi } from "vitest";
import { confirmDeleteSendIdentity } from "@mylomail/renderer/Components/AccountSettings/SendIdentityManager/SendIdentityManager";

describe("confirmDeleteSendIdentity", () => {
	it("asks for confirmation naming the identity, and proceeds when confirmed", () => {
		const confirm = vi.fn().mockReturnValue(true);
		const proceed = confirmDeleteSendIdentity(
			{ displayName: "Work", emailAddress: "work@example.test" },
			confirm,
		);
		expect(confirm).toHaveBeenCalledWith(
			expect.stringContaining("Work <work@example.test>"),
		);
		expect(proceed).toBe(true);
	});

	it("does not proceed when the user cancels", () => {
		const confirm = vi.fn().mockReturnValue(false);
		const proceed = confirmDeleteSendIdentity(
			{ displayName: "Work", emailAddress: "work@example.test" },
			confirm,
		);
		expect(proceed).toBe(false);
	});
});
