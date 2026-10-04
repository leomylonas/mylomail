import { describe, expect, it } from "vitest";
import {
	isSharedMailboxAddress,
	microsoftAddressFields,
	microsoftAddressReady,
} from "@mylomail/renderer/Components/AddAccount/SharedMailbox";

describe("isSharedMailboxAddress", () => {
	it.each([
		"support@contoso.com",
		"first.last+tag@sub.contoso.co.uk",
		" a@b.io ",
	])("accepts %s", (value) => {
		expect(isSharedMailboxAddress(value)).toBe(true);
	});

	it.each([
		"",
		"support",
		"support@contoso",
		"two words@contoso.com",
		"support@contoso.com/../me",
		"Support <support@contoso.com>",
	])("rejects %j", (value) => {
		expect(isSharedMailboxAddress(value)).toBe(false);
	});
});

describe("microsoftAddressFields", () => {
	it("sends the address untouched for an ordinary account", () => {
		expect(
			microsoftAddressFields({
				emailAddress: "me@contoso.com",
				isSharedMailbox: false,
			}),
		).toEqual({
			emailAddress: "me@contoso.com",
			microsoftSharedMailbox: undefined,
		});
	});

	it("sends the same trimmed address as both the account address and the shared mailbox", () => {
		expect(
			microsoftAddressFields({
				emailAddress: "  support@contoso.com ",
				isSharedMailbox: true,
			}),
		).toEqual({
			emailAddress: "support@contoso.com",
			microsoftSharedMailbox: "support@contoso.com",
		});
	});
});

describe("microsoftAddressReady", () => {
	it("requires a well-formed address when the mailbox is shared", () => {
		const shared = { isSharedMailbox: true };

		expect(microsoftAddressReady({ ...shared, emailAddress: "" })).toBe(false);
		expect(microsoftAddressReady({ ...shared, emailAddress: "support" })).toBe(
			false,
		);
		expect(
			microsoftAddressReady({ ...shared, emailAddress: "support@contoso.com" }),
		).toBe(true);
	});

	it("accepts any non-empty address otherwise", () => {
		const personal = { isSharedMailbox: false };

		expect(microsoftAddressReady({ ...personal, emailAddress: "" })).toBe(
			false,
		);
		expect(
			microsoftAddressReady({ ...personal, emailAddress: "me@contoso.com" }),
		).toBe(true);
	});
});
