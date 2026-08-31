import Store from "react-granular-store";

/** Which mailbox and message this window is looking at, and how its panels are sized. */
export interface WindowState {
	selectedAccountId: string | null;
	selectedMailboxId: string | null;
	selectedMessageId: string | null;
	sidebarWidth: number;
}

/**
 * Client-only UI state for <b>one window</b>.
 *
 * <b>Never a module-level singleton.</b> Multi-window is an assumption here, not a feature
 * added later: two windows showing different mailboxes is the ordinary case, and a shared
 * module-scoped store would have one window's selection silently move the other's. Creating
 * it per window makes that impossible rather than merely discouraged.
 */
export function createWindowStore(): Store<WindowState> {
	return new Store<WindowState>({
		selectedAccountId: null,
		selectedMailboxId: null,
		selectedMessageId: null,
		sidebarWidth: 260,
	});
}
