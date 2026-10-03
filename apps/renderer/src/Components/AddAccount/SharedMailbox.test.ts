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
	it("sends the personal address untouched for an ordinary account", () => {
		expect(
			microsoftAddressFields({
				emailAddress: "me@contoso.com",
				isSharedMailbox: false,
				sharedMailbox: "support@contoso.com",
			}),
		).toEqual({
			emailAddress: "me@contoso.com",
			microsoftSharedMailbox: undefined,
		});
	});

	it("sends the shared address as both the account address and the shared mailbox", () => {
		expect(
			microsoftAddressFields({
				emailAddress: "me@contoso.com",
				isSharedMailbox: true,
				sharedMailbox: "  support@contoso.com ",
			}),
		).toEqual({
			emailAddress: "support@contoso.com",
			microsoftSharedMailbox: "support@contoso.com",
		});
	});
});

describe("microsoftAddressReady", () => {
	it("requires a valid shared address, not the personal one, when shared", () => {
		const shared = { emailAddress: "me@contoso.com", isSharedMailbox: true };

		expect(microsoftAddressReady({ ...shared, sharedMailbox: "" })).toBe(false);
		expect(microsoftAddressReady({ ...shared, sharedMailbox: "support" })).toBe(
			false,
		);
		expect(
			microsoftAddressReady({
				...shared,
				sharedMailbox: "support@contoso.com",
			}),
		).toBe(true);
	});

	it("requires the personal address, not the shared one, otherwise", () => {
		const personal = {
			isSharedMailbox: false,
			sharedMailbox: "support@contoso.com",
		};

		expect(microsoftAddressReady({ ...personal, emailAddress: "" })).toBe(
			false,
		);
		expect(
			microsoftAddressReady({ ...personal, emailAddress: "me@contoso.com" }),
		).toBe(true);
	});
});
