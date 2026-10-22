import { ProviderType } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

/**
 * Whether mail can be moved into an account of this type. Microsoft 365 cannot store a received
 * message as it was (creating one from MIME always makes a draft), so a move into it is refused;
 * the backend enforces the same rule and this only keeps the UI from offering it.
 */
export function canReceiveMovedMail(providerType?: ProviderType): boolean {
	return providerType !== ProviderType.Microsoft365;
}

export const microsoft365CannotReceive =
	"Mail can't be moved into a Microsoft 365 account: it can't store a received message as it was.";
