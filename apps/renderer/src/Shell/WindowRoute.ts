import {
	parseMailtoUri,
	type MailtoComposeRequest,
} from "@mylomail/electron-shell/Mailto";

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
			initialMailto?: MailtoComposeRequest;
	  };

export interface WindowSearch {
	message?: string;
	subject?: string;
	sender?: string;
	compose?: string;
	account?: string;
	notification?: string;
	mailto?: string;
}

export function parseWindowRoute(search: string): WindowRoute {
	const params = new URLSearchParams(search);
	return windowRouteFromSearch({
		message: params.get("message") ?? undefined,
		subject: params.get("subject") ?? undefined,
		sender: params.get("sender") ?? undefined,
		compose: params.get("compose") ?? undefined,
		account: params.get("account") ?? undefined,
		notification: params.get("notification") ?? undefined,
		mailto: params.get("mailto") ?? undefined,
	});
}

export function windowRouteFromSearch(search: WindowSearch): WindowRoute {
	if (search.message) {
		return {
			kind: "message",
			messageId: search.message,
			subject: search.subject ?? "",
			// Carried over from the window that popped this one out — see `AppShell`'s
			// `onOpenInNewWindow` and `MessageWindow`'s own doc comment. Without it, a sender
			// already on the persisted remote-content allow list would still be blocked and
			// re-prompted in this window, contradicting that allow list's whole point.
			senderAddress: search.sender,
		};
	}

	if (search.compose && search.account) {
		return {
			kind: "compose",
			draftId: search.compose,
			accountId: search.account,
		};
	}
	if (search.notification && search.account) {
		return {
			kind: "shell",
			initialNotification: {
				notificationId: search.notification,
				accountId: search.account,
			},
		};
	}
	if (search.mailto) {
		const initialMailto = parseMailtoUri(search.mailto);
		if (initialMailto) return { kind: "shell", initialMailto };
	}

	return { kind: "shell" };
}
