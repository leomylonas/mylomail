import Store from "react-granular-store";

/** Which mailbox and message this window is looking at, and how its panels are sized. */
export interface WindowState {
	selectedAccountId: string | null;
	selectedMailboxId: string | null;
	selectedMessageId: string | null;

	/**
	 * The selected message's subject.
	 *
	 * Held beside the id because the reading pane has no way to ask for one message: the hub
	 * offers a list and a body, and the subject belongs to neither. Kept in the window store
	 * rather than component state so it cannot drift from the selection it describes.
	 */
	selectedMessageSubject: string;

	/**
	 * The selected message's first `From` address, held for the same reason as
	 * {@link selectedMessageSubject}: the reading pane needs it (to check/offer the
	 * remote-content allow list) but has no way to ask for one message on its own.
	 */
	selectedMessageSenderAddress: string;
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
		selectedMessageSubject: "",
		selectedMessageSenderAddress: "",
	});
}
