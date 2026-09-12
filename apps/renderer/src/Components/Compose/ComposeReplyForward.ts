import { prepare } from "@mylomail/renderer/Components/MessageHtml/SanitiseMessageHtml";
import { fetchApi } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";

export interface Address {
	name: string | null;
	email: string;
}

/** The address/subject/date fields a reply or forward is built from — matches
 * `MessageReplyContextDto` (§13). */
export interface MessageReplyContext {
	messageId: string;
	accountId: string;
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
	/** The `cid:` value an inline attachment is referenced by; null for a plain one. */
	contentId: string | null;
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
	/**
	 * Attachments to copy onto the new draft once it exists: every attachment for a forward,
	 * only inline ones for a reply (a reply's quote references the original's inline images via
	 * the `cid:`s `sanitiseForQuoting` preserves, but never means to drag along its ordinary
	 * attachments too — that's what forwarding is for).
	 */
	attachmentsToCopy?: {
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

/**
 * Whether the compose body mentions an attachment, ignoring quoted/forwarded content: both
 * {@link buildReplySeed} and {@link buildForwardSeed} always wrap the original message in a
 * `<blockquote>`, and forwarded mail in particular almost always says "see attached" about
 * *its own* attachments — scanning the raw HTML would make Epic 6's missing-attachment warning
 * fire on nearly every reply/forward regardless of what the user actually typed, which isn't
 * a heuristic anyone would keep switched on.
 */
export function mentionsAttachmentOutsideQuote(html: string): boolean {
	const document = new DOMParser().parseFromString(html, "text/html");
	document
		.querySelectorAll("blockquote")
		.forEach((element) => element.remove());
	return /\b(attached|attachment|attach)\b/i.test(
		document.body.textContent ?? "",
	);
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

/**
 * Who a reply/reply-all actually goes to, split out from {@link buildReplySeed} so it can be
 * tested without pulling in `sanitiseForQuoting`'s DOMPurify dependency, which needs a real DOM.
 */
export function buildReplyRecipients(
	mode: "reply" | "replyAll",
	context: MessageReplyContext,
	ownAddress: string,
): { to: Address[]; cc: Address[] } {
	const to = excludeSelf(dedupe(replyTarget(context)), ownAddress);
	const cc =
		mode === "replyAll"
			? excludeSelf(dedupe([...context.to, ...context.cc]), ownAddress).filter(
					(address) =>
						!to.some(
							(t) => t.email.toLowerCase() === address.email.toLowerCase(),
						),
				)
			: [];
	return { to, cc };
}

/** Builds a Gmail/Outlook-style quoted reply (§13). */
export function buildReplySeed(
	mode: "reply" | "replyAll",
	context: MessageReplyContext,
	originalBodyHtml: string,
	ownAddress: string,
	attachments: ForwardAttachment[] = [],
): ComposeSeed {
	const { to, cc } = buildReplyRecipients(mode, context, ownAddress);

	const attribution = `On ${formatWhen(context.receivedAt)}, ${formatAddress(
		context.from[0] ?? { name: null, email: "" },
	)} wrote:`;
	const quoted = sanitiseForQuoting(originalBodyHtml);
	const bodyHtml =
		`<p><br></p><p>${attribution}</p>` +
		`<blockquote style="margin:0 0 0 0.8ex;border-left:2px solid #ccc;padding-left:1ex;">` +
		`${quoted}</blockquote>`;

	// Only the inline images the preserved cid:s in `quoted` can actually reference — a reply
	// never means to drag along the original's ordinary attachments too.
	const inline = attachments.filter((attachment) => attachment.isInline);

	return {
		key: `reply-${context.messageId}-${mode}-${Date.now()}`,
		to,
		cc,
		bcc: [],
		subject: prefixSubject(context.subject, "Re"),
		bodyHtml,
		inReplyToMessageId: context.messageId,
		attachmentsToCopy:
			inline.length > 0
				? { sourceMessageId: context.messageId, attachments: inline }
				: undefined,
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

	return {
		key: `forward-${context.messageId}-${Date.now()}`,
		to: [],
		cc: [],
		bcc: [],
		subject: prefixSubject(context.subject, "Fwd"),
		bodyHtml,
		// A forward is a new, unrelated message — never threaded to the original (§1).
		inReplyToMessageId: null,
		// Every attachment, inline or not: an inline image's cid: is preserved in `quoted` above
		// the same way a reply's is, and a forward's ordinary attachments are the whole point of
		// forwarding — unlike a reply, there's no attachment a forward means to leave behind.
		attachmentsToCopy:
			attachments.length > 0
				? { sourceMessageId: context.messageId, attachments }
				: undefined,
	};
}

/**
 * Copies a reply's or forward's attachments onto a just-created draft — there is no
 * "attach this other message's attachment" concept, only "upload bytes," so this reads each one
 * back from the original message and re-uploads it the same way a dropped file would be,
 * preserving `isInline`/`contentId` so an inline image keeps the same `cid:` binding its copy of
 * the quoted HTML already references. Shared by `Compose`'s own inline reply/forward flow and a
 * popped-out message window's own reply/forward, which has no `Compose` instance mounted to run
 * that flow for it (§13 Epic 10). Each attachment is copied independently: one failing (a
 * since-deleted attachment, a network blip) must not silently drop the rest of a
 * multi-attachment reply/forward — the caller decides what to do with the list of names that
 * failed.
 */
export async function copyAttachments(
	draftId: string,
	toCopy: NonNullable<ComposeSeed["attachmentsToCopy"]>,
): Promise<string[]> {
	const failed: string[] = [];
	for (const attachment of toCopy.attachments) {
		try {
			const response = await fetchApi(
				`/messages/${toCopy.sourceMessageId}/attachments/${attachment.id}`,
			);
			const form = new FormData();
			form.append("file", await response.blob(), attachment.filename);
			if (attachment.isInline) {
				form.append("isInline", "true");
				if (attachment.contentId) {
					form.append("contentId", attachment.contentId);
				}
			}
			await fetchApi(`/drafts/${draftId}/attachments`, {
				method: "POST",
				body: form,
			});
		} catch {
			failed.push(attachment.filename);
		}
	}
	return failed;
}
