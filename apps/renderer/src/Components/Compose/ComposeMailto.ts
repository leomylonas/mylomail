import type { MailtoComposeRequest } from "@mylomail/electron-shell/Mailto";
import {
	type Address,
	type ComposeSeed,
} from "@mylomail/renderer/Components/Compose/ComposeReplyForward";

/** Converts an OS mailto activation into the same unsaved seed used by reply and forward. */
export function buildMailtoSeed(request: MailtoComposeRequest): ComposeSeed {
	return {
		key: `mailto-${crypto.randomUUID()}`,
		to: addresses(request.to),
		cc: addresses(request.cc),
		bcc: addresses(request.bcc),
		subject: request.subject,
		bodyHtml: request.body ? `<p>${escapeBody(request.body)}</p>` : "",
		inReplyToMessageId: null,
	};
}

function addresses(values: readonly string[]): Address[] {
	return values.map((email) => ({ name: null, email }));
}

function escapeBody(body: string): string {
	return body
		.replaceAll("&", "&amp;")
		.replaceAll("<", "&lt;")
		.replaceAll(">", "&gt;")
		.replaceAll('"', "&quot;")
		.replaceAll("'", "&#39;")
		.replace(/\r\n|\r|\n/g, "<br>");
}
