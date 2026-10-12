import type { Mailbox } from "@mylomail/renderer/Components/MailboxTree/MailboxTree";
import { SpecialUse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";

const SYSTEM_ORDER = [
	SpecialUse.Inbox,
	SpecialUse.Drafts,
	SpecialUse.Sent,
	SpecialUse.Archive,
	SpecialUse.Junk,
	SpecialUse.Trash,
];

/** Sort key putting system folders first, in a fixed order; everything else ties last. */
export function systemRank(mailbox: Mailbox): number {
	const index = SYSTEM_ORDER.indexOf(
		mailbox.specialUseOverride ?? mailbox.specialUse,
	);
	return index === -1 ? SYSTEM_ORDER.length : index;
}

/** IMAP names the inbox "INBOX" by protocol; show it in title case. */
export function displayMailboxName(mailbox: Mailbox): string {
	return mailbox.name.toUpperCase() === "INBOX" ? "Inbox" : mailbox.name;
}

/** Unread count of an account's inbox — what its header shows while the tree is collapsed. */
export function inboxUnreadCount(mailboxes: readonly Mailbox[]): number {
	return mailboxes
		.filter(
			(mailbox) =>
				(mailbox.specialUseOverride ?? mailbox.specialUse) === SpecialUse.Inbox,
		)
		.reduce((sum, mailbox) => sum + mailbox.unreadCount, 0);
}

export interface PinnedMailbox {
	mailbox: Mailbox;
	accountId: string;
	label: string;
	/** The account name, only when another pinned folder would otherwise read the same. */
	accountQualifier: string | null;
}

/**
 * The pinned order after dropping `draggedId` immediately before `targetId`, or null when the
 * drop would change nothing.
 */
export function movePinnedBefore(
	orderedIds: readonly string[],
	draggedId: string,
	targetId: string,
): string[] | null {
	if (draggedId === targetId) return null;
	const without = orderedIds.filter((id) => id !== draggedId);
	const at = without.indexOf(targetId);
	if (at === -1 || !orderedIds.includes(draggedId)) return null;
	without.splice(at, 0, draggedId);
	return without.every((id, index) => id === orderedIds[index])
		? null
		: without;
}

/**
 * Pinned folders across accounts in the user's own order; folders that tie (pinned before
 * ordering existed) fall back to account order, then system-first within the account.
 * Two pinned folders with the same displayed name (two inboxes) each carry their account name
 * so they can be told apart; a unique name stays bare.
 */
export function pinnedMailboxes(
	accounts: readonly { id: string; displayName: string }[],
	mailboxesByAccount: ReadonlyMap<string, readonly Mailbox[]>,
): PinnedMailbox[] {
	const entries = accounts.flatMap((account) =>
		(mailboxesByAccount.get(account.id) ?? [])
			.filter((mailbox) => mailbox.isPinned)
			.sort((a, b) => systemRank(a) - systemRank(b))
			.map((mailbox) => ({
				mailbox,
				accountId: account.id,
				accountName: account.displayName,
				label: displayMailboxName(mailbox),
			})),
	);
	entries.sort((a, b) => a.mailbox.pinnedSortOrder - b.mailbox.pinnedSortOrder);
	const counts = new Map<string, number>();
	for (const { label } of entries) {
		counts.set(label, (counts.get(label) ?? 0) + 1);
	}
	return entries.map(({ accountName, ...entry }) => ({
		...entry,
		accountQualifier: (counts.get(entry.label) ?? 0) > 1 ? accountName : null,
	}));
}
