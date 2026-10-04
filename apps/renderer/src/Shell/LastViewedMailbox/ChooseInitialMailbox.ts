/** The folder the backend remembers, already resolved against current topology. */
export interface RememberedMailbox {
	accountId?: string | null;
	mailboxId?: string | null;
}

export interface InitialMailboxSelection {
	accountId: string;
	mailboxId: string;
}

/**
 * Which folder a window opens on, if it should open on one at all.
 *
 * Only a plain main shell window restores the remembered folder: a window opened for a
 * purpose (a notification click, a mailto link) is there to show something specific, and
 * must neither move to nor later overwrite the remembered folder. The remembered account
 * also has to be one this window can show, otherwise the caller falls back to its ordinary
 * default selection. The backend has already dropped a mailbox that no longer exists, so
 * absence here covers a removed mailbox as well as "nothing remembered yet".
 */
export function chooseInitialMailbox({
	plainShell,
	remembered,
	accounts,
}: {
	plainShell: boolean;
	remembered: RememberedMailbox | null | undefined;
	accounts: readonly { id: string; isEnabled?: boolean }[];
}): InitialMailboxSelection | null {
	if (!plainShell || !remembered?.accountId || !remembered.mailboxId) {
		return null;
	}

	const { accountId, mailboxId } = remembered;
	const account = accounts.find((candidate) => candidate.id === accountId);
	if (!account || account.isEnabled === false) return null;

	return { accountId, mailboxId };
}
