/**
 * A Microsoft 365 shared mailbox is connected by signing in as yourself and addressing the
 * shared mailbox, so the account's own address *is* the shared address. The form has one email
 * field for both cases and a switch that says which kind of mailbox it names. These helpers keep
 * that rule, and the shape check the server enforces, in one testable place.
 */

/** Mirrors `GraphMailbox.IsValidSharedMailbox` on the server: a plain `local@domain.tld`. */
const sharedMailboxPattern =
	/^[A-Za-z0-9._+'&=-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)+$/;

const maximumAddressLength = 320;

export function isSharedMailboxAddress(value: string): boolean {
	const address = value.trim();
	return (
		address.length <= maximumAddressLength && sharedMailboxPattern.test(address)
	);
}

export interface MicrosoftAddressInput {
	emailAddress: string;
	isSharedMailbox: boolean;
}

/**
 * The address fields of an add-account request for a Microsoft 365 account. For a shared
 * mailbox the one address is sent as both `emailAddress` (the account's default send identity)
 * and `microsoftSharedMailbox`.
 */
export function microsoftAddressFields(input: MicrosoftAddressInput): {
	emailAddress: string;
	microsoftSharedMailbox: string | undefined;
} {
	if (!input.isSharedMailbox) {
		return {
			emailAddress: input.emailAddress,
			microsoftSharedMailbox: undefined,
		};
	}

	const address = input.emailAddress.trim();
	return { emailAddress: address, microsoftSharedMailbox: address };
}

/** Whether the address the account will be created with is usable. */
export function microsoftAddressReady(input: MicrosoftAddressInput): boolean {
	return input.isSharedMailbox
		? isSharedMailboxAddress(input.emailAddress)
		: Boolean(input.emailAddress);
}
