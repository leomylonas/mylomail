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

	/** Whether this window shows every message or collapses conversations to a representative. */
	messageListThreadMode: "flat" | "collapsed";
}
const defaultThreadMode: WindowState["messageListThreadMode"] = "flat";

export interface ThreadModePersistence {
	read(): WindowState["messageListThreadMode"] | null;
	write(value: WindowState["messageListThreadMode"]): void;
}

class CookieThreadModePersistence implements ThreadModePersistence {
	private readonly name: string;

	constructor(slot: string) {
		this.name = `mylomail_thread_mode_${encodeURIComponent(slot)}`;
	}

	read(): WindowState["messageListThreadMode"] | null {
		const prefix = `${this.name}=`;
		const value = document.cookie
			.split("; ")
			.find((cookie) => cookie.startsWith(prefix))
			?.slice(prefix.length);
		return value === "collapsed" || value === "flat" ? value : null;
	}

	write(value: WindowState["messageListThreadMode"]): void {
		document.cookie = `${this.name}=${value}; Max-Age=31536000; Path=/; SameSite=Strict`;
	}
}

function browserThreadModePersistence(): ThreadModePersistence | null {
	if (typeof window === "undefined") return null;
	const slot =
		new URLSearchParams(window.location.search).get("windowSlot") ?? "primary";
	return new CookieThreadModePersistence(slot);
}

function loadThreadMode(
	persistence: ThreadModePersistence | null,
): WindowState["messageListThreadMode"] {
	if (persistence === null) return defaultThreadMode;
	try {
		return persistence.read() ?? defaultThreadMode;
	} catch {
		return defaultThreadMode;
	}
}

/**
 * Client-only UI state for <b>one window</b>.
 *
 * <b>Never a module-level singleton.</b> Multi-window is an assumption here, not a feature
 * added later: two windows showing different mailboxes is the ordinary case, and a shared
 * module-scoped store would have one window's selection silently move the other's. Creating
 * it per window makes that impossible rather than merely discouraged.
 */
export function createWindowStore(
	persistence = browserThreadModePersistence(),
): Store<WindowState> {
	const store = new Store<WindowState>({
		selectedAccountId: null,
		selectedMailboxId: null,
		selectedMessageId: null,
		selectedMessageSubject: "",
		selectedMessageSenderAddress: "",
		messageListThreadMode: loadThreadMode(persistence),
	});
	store.subscribe("messageListThreadMode", () => {
		if (persistence === null) return;
		try {
			persistence.write(store.getState("messageListThreadMode"));
		} catch {
			// Storage can be disabled; the setting still remains window-local for this run.
		}
	});
	return store;
}
