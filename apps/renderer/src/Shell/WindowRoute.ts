/**
 * Which window kind a query string names, and the params it carries (§13 Epic 10).
 *
 * Pulled out of `Main.tsx` as pure, side-effect-free parsing so it can be unit tested directly —
 * `Main.tsx` itself calls `createRoot`/`document.getElementById` at import time, which makes it
 * unusable from a test file.
 */
export type WindowRoute =
	| {
			kind: "message";
			messageId: string;
			subject: string;
			senderAddress: string | undefined;
	  }
	| { kind: "compose"; draftId: string; accountId: string }
	| {
			kind: "shell";
			initialNotification?: { notificationId: string; accountId: string };
	  };

export function parseWindowRoute(search: string): WindowRoute {
	const params = new URLSearchParams(search);
	const message = params.get("message");
	if (message) {
		return {
			kind: "message",
			messageId: message,
			subject: params.get("subject") ?? "",
			// Carried over from the window that popped this one out — see `AppShell`'s
			// `onOpenInNewWindow` and `MessageWindow`'s own doc comment. Without it, a sender
			// already on the persisted remote-content allow list would still be blocked and
			// re-prompted in this window, contradicting that allow list's whole point.
			senderAddress: params.get("sender") ?? undefined,
		};
	}

	const compose = params.get("compose");
	const account = params.get("account");
	if (compose && account) {
		return { kind: "compose", draftId: compose, accountId: account };
	}
	const notification = params.get("notification");
	if (notification && account) {
		return {
			kind: "shell",
			initialNotification: {
				notificationId: notification,
				accountId: account,
			},
		};
	}

	return { kind: "shell" };
}
