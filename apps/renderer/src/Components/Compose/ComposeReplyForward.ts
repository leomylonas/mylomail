import { prepare } from "@mylomail/renderer/Components/MessageHtml/SanitiseMessageHtml";

export interface Address {
	name: string | null;
	email: string;
}

/** The address/subject/date fields a reply or forward is built from — matches
 * `MessageReplyContextDto` (§13). */
export interface MessageReplyContext {
	messageId: string;
	from: Address[];
	to: Address[];
	cc: Address[];
	replyTo: Address[];
	subject: string;
	receivedAt: string;
}

export interface ForwardAttachment {
	id: string;
	filename: string;
	mimeType: string;
	isInline: boolean;
}

/**
 * What `Compose` needs to open prefilled as a reply or forward, before any draft has been
 * saved to the server (§13).
 */
export interface ComposeSeed {
	/**
	 * Forces `Compose` to remount for each new reply/forward, the same way switching to an
	 * existing draft's real id already does — without this, opening "Reply" on a second
	 * message while the first reply is still unsaved would reuse the mounted instance's stale
	 * state instead of starting fresh.
	 */
	key: string;
	to: Address[];
	cc: Address[];
	bcc: Address[];
	subject: string;
	bodyHtml: string;
	inReplyToMessageId: string | null;
	/** Present only for a forward: attachments to copy onto the new draft once it exists. */
	forwardAttachments?: {
		sourceMessageId: string;
		attachments: ForwardAttachment[];
	};
}

/** Reply routing prefers `replyTo` over `from` when present (§1). */
function replyTarget(context: MessageReplyContext): Address[] {
	return context.replyTo.length > 0 ? context.replyTo : context.from;
}

/** Drops any address matching the account's own — replying to yourself is never intended. */
function excludeSelf(addresses: Address[], ownAddress: string): Address[] {
	const lowered = ownAddress.toLowerCase();
	return addresses.filter((address) => address.email.toLowerCase() !== lowered);
}

/** De-duplicates by address, keeping the first occurrence (and its display name, if any). */
function dedupe(addresses: Address[]): Address[] {
	const seen = new Set<string>();
	const result: Address[] = [];
	for (const address of addresses) {
		const key = address.email.toLowerCase();
		if (seen.has(key)) continue;
		seen.add(key);
		result.push(address);
	}
	return result;
}

/** "Re: X" stays "Re: X", never "Re: Re: X" — checked case-insensitively. */
function prefixSubject(subject: string, prefix: string): string {
	const trimmed = subject.trim();
	return new RegExp(`^${prefix}\\s*:`, "i").test(trimmed)
		? trimmed
		: `${prefix}: ${trimmed}`;
}

function escapeHtml(text: string): string {
	return text
		.replace(/&/g, "&amp;")
		.replace(/</g, "&lt;")
		.replace(/>/g, "&gt;")
		.replace(/"/g, "&quot;");
}

/** The original message's body as `GetMessageBody` returned it (§13). */
export interface MessageBody {
	html: string | null;
	text: string | null;
	isFetched: boolean;
	isFailed: boolean;
}

/**
 * Resolves what a reply/forward should actually quote, honestly reflecting whatever
 * `GetMessageBody` is currently able to say — never a silent blank.
 */
export function resolveOriginalHtml(body: MessageBody): string {
	if (body.isFetched && body.html) return body.html;
	if (body.isFetched && body.text) {
		return `<p>${escapeHtml(body.text).replace(/\n/g, "<br>")}</p>`;
	}
	return body.isFailed
		? "<p><em>(This message's content could not be downloaded, so it cannot be quoted.)</em></p>"
		: "<p><em>(This message's content is still downloading and cannot be quoted yet — try again in a moment.)</em></p>";
}

function formatAddress(address: Address): string {
	return address.name
		? `${escapeHtml(address.name)} &lt;${escapeHtml(address.email)}&gt;`
		: escapeHtml(address.email);
}

function formatAddressList(addresses: Address[]): string {
	return addresses.map(formatAddress).join(", ");
}

function formatWhen(receivedAt: string): string {
	return new Date(receivedAt).toLocaleString(undefined, {
		weekday: "short",
		year: "numeric",
		month: "short",
		day: "numeric",
		hour: "numeric",
		minute: "2-digit",
	});
}

/**
 * Sanitised before quoting regardless of the original message's own remote-content trust
 * state (§13 Epic 5): a reply/forward draft is an editable document, not the isolated,
 * read-only frame `MessageHtml` renders received mail in, so silently carrying over a live
 * tracking pixel or `<script>` into something the user is about to author into and send would
 * bypass that isolation entirely. Blocking remote content here costs nothing the user cannot
 * already see and re-allow in the original message's own reading-pane view.
 */
function sanitiseForQuoting(html: string): string {
	return prepare(html, false).html;
}

/** Builds a Gmail/Outlook-style quoted reply (§13). */
export function buildReplySeed(
	mode: "reply" | "replyAll",
	context: MessageReplyContext,
	originalBodyHtml: string,
	ownAddress: string,
): ComposeSeed {
	const to = excludeSelf(replyTarget(context), ownAddress);
	const cc =
		mode === "replyAll"
			? excludeSelf(dedupe([...context.to, ...context.cc]), ownAddress).filter(
					(address) =>
						!to.some(
							(t) => t.email.toLowerCase() === address.email.toLowerCase(),
						),
				)
			: [];

	const attribution = `On ${formatWhen(context.receivedAt)}, ${formatAddress(
		context.from[0] ?? { name: null, email: "" },
	)} wrote:`;
	const quoted = sanitiseForQuoting(originalBodyHtml);
	const bodyHtml =
		`<p><br></p><p>${attribution}</p>` +
		`<blockquote style="margin:0 0 0 0.8ex;border-left:2px solid #ccc;padding-left:1ex;">` +
		`${quoted}</blockquote>`;

	return {
		key: `reply-${context.messageId}-${mode}-${Date.now()}`,
		to,
		cc,
		bcc: [],
		subject: prefixSubject(context.subject, "Re"),
		bodyHtml,
		inReplyToMessageId: context.messageId,
	};
}

/** Builds a Gmail-style forwarded message (§13). */
export function buildForwardSeed(
	context: MessageReplyContext,
	originalBodyHtml: string,
	attachments: ForwardAttachment[],
): ComposeSeed {
	const header =
		`<p>---------- Forwarded message ---------<br>` +
		`From: ${formatAddressList(context.from)}<br>` +
		`Date: ${escapeHtml(formatWhen(context.receivedAt))}<br>` +
		`Subject: ${escapeHtml(context.subject)}<br>` +
		`To: ${formatAddressList(context.to)}</p>`;
	const bodyHtml = `<p><br></p>${header}<p><br></p>${sanitiseForQuoting(originalBodyHtml)}`;

	const forwardable = attachments.filter((attachment) => !attachment.isInline);

	return {
		key: `forward-${context.messageId}-${Date.now()}`,
		to: [],
		cc: [],
		bcc: [],
		subject: prefixSubject(context.subject, "Fwd"),
		bodyHtml,
		// A forward is a new, unrelated message — never threaded to the original (§1).
		inReplyToMessageId: null,
		forwardAttachments:
			forwardable.length > 0
				? { sourceMessageId: context.messageId, attachments: forwardable }
				: undefined,
	};
}
