import { describe, expect, it } from "vitest";
import { ErrorCategory } from "@mylomail/shared-types/SignalR/MyloMail.Api.Errors";
import { present } from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";

describe("error presentation", () => {
	/**
	 * Every category must map to something. A category with no mapping would reach the user as
	 * an empty toast, which is worse than the raw error it replaced.
	 */
	it("maps every category to a title and detail", () => {
		const categories = Object.values(ErrorCategory).filter(
			(value): value is ErrorCategory => typeof value === "number",
		);

		expect(categories.length).toBeGreaterThan(0);
		for (const category of categories) {
			const presentation = present(category, null);
			expect(presentation.title).not.toBe("");
			expect(presentation.detail).not.toBe("");
		}
	});

	/**
	 * A notification that carries an action must persist. Carbon's ToastNotification
	 * auto-dismisses and must contain no interactive content — an action inside one disappears
	 * while still reachable by keyboard, which is the WCAG failure §13 calls out by name.
	 */
	it("never marks an actionable failure as transient", () => {
		const categories = Object.values(ErrorCategory).filter(
			(value): value is ErrorCategory => typeof value === "number",
		);

		for (const category of categories) {
			const presentation = present(category, null);
			if (presentation.action) expect(presentation.transient).toBe(false);
		}
	});

	/** Connectivity resolves itself, so it must not demand anything of the user (§15). */
	it("treats a network failure as passing weather", () => {
		const presentation = present(ErrorCategory.Network, null);

		expect(presentation.transient).toBe(true);
		expect(presentation.action).toBeUndefined();
	});

	/** Auth is the opposite: nothing recovers until the user acts. */
	it("asks the user to sign in again on an auth failure", () => {
		const presentation = present(ErrorCategory.Auth, null);

		expect(presentation.action).toBe("reauthenticate");
		expect(presentation.transient).toBe(false);
	});

	/** A conflict is resolved by the user choosing, never by the app merging (§1, §15). */
	it("offers resolution on a conflict rather than resolving it", () => {
		const presentation = present(ErrorCategory.Conflict, null);

		expect(presentation.action).toBe("resolve");
	});

	it("prefers the server's own detail when there is one", () => {
		expect(
			present(ErrorCategory.ProviderRejected, "Mailbox is full").detail,
		).toBe("Mailbox is full");
	});
});
