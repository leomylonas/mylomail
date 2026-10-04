import { describe, expect, it } from "vitest";
import { chooseInitialMailbox } from "@mylomail/renderer/Shell/LastViewedMailbox/ChooseInitialMailbox";

const accounts = [{ id: "account-a" }, { id: "account-b" }];

describe("chooseInitialMailbox", () => {
	it("opens a plain shell window on the remembered folder", () => {
		expect(
			chooseInitialMailbox({
				plainShell: true,
				remembered: { accountId: "account-b", mailboxId: "mailbox-b" },
				accounts,
			}),
		).toEqual({ accountId: "account-b", mailboxId: "mailbox-b" });
	});

	it("falls back to the default when nothing is remembered", () => {
		for (const remembered of [
			undefined,
			null,
			{ accountId: null, mailboxId: null },
			{ accountId: "account-a", mailboxId: null },
			{ accountId: null, mailboxId: "mailbox-a" },
		]) {
			expect(
				chooseInitialMailbox({ plainShell: true, remembered, accounts }),
			).toBeNull();
		}
	});

	it("falls back to the default when the remembered account is gone", () => {
		expect(
			chooseInitialMailbox({
				plainShell: true,
				remembered: { accountId: "removed", mailboxId: "mailbox-x" },
				accounts,
			}),
		).toBeNull();
	});

	it("falls back to the default when the remembered account is disabled", () => {
		expect(
			chooseInitialMailbox({
				plainShell: true,
				remembered: { accountId: "account-a", mailboxId: "mailbox-a" },
				accounts: [{ id: "account-a", isEnabled: false }],
			}),
		).toBeNull();
	});

	it("treats an account that does not say otherwise as enabled", () => {
		expect(
			chooseInitialMailbox({
				plainShell: true,
				remembered: { accountId: "account-a", mailboxId: "mailbox-a" },
				accounts: [{ id: "account-a", isEnabled: true }, { id: "account-b" }],
			}),
		).toEqual({ accountId: "account-a", mailboxId: "mailbox-a" });
	});

	it("never applies the remembered folder to a window opened for a purpose", () => {
		expect(
			chooseInitialMailbox({
				plainShell: false,
				remembered: { accountId: "account-a", mailboxId: "mailbox-a" },
				accounts,
			}),
		).toBeNull();
	});
});
