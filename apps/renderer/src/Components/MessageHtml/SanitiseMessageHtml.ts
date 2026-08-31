import DOMPurify from "dompurify";

/** The result of preparing a message body for display. */
export interface PreparedHtml {
	html: string;

	/** How many remote references were withheld, so the UI can offer to load them. */
	blockedRemoteCount: number;
}

/**
 * Attributes that fetch something. Every one of these must be neutralised, not just `src`.
 *
 * §13 is explicit that removing `<img src>` is not enough: `srcset`, CSS `url()`, SVG
 * references and `@import` all issue requests, and a tracking pixel hidden in a background
 * image is exactly as effective as one in an `<img>`.
 */
const fetchingAttributes = [
	"src",
	"srcset",
	"poster",
	"background",
	"data-src",
];

/**
 * The schemes a message body may reference.
 *
 * DOMPurify's default set does not include `blob:`, which would silently strip the inline
 * images this app fetches itself — the only kind of image it trusts. Everything else is the
 * default: http(s) and mailto for links, cid for parts that have not been resolved yet.
 */
const allowedSchemes =
	/^(?:blob:|cid:|mailto:|tel:|https?:|[^a-z]|[a-z+.-]+(?:[^a-z+.\-:]|$))/i;

/**
 * Sanitises a message body and withholds its remote content.
 *
 * <b>Two independent defences, on purpose.</b> This strips what should never run; the
 * iframe's CSP refuses the requests anyway. Sanitisation alone is a parser competing with an
 * attacker who only has to win once, and a policy alone would still leave scripts in the DOM
 * of an isolated document. Neither is sufficient, so both are applied.
 */
export function prepare(html: string, allowRemote: boolean): PreparedHtml {
	let blockedRemoteCount = 0;

	DOMPurify.addHook("uponSanitizeElement", (node) => {
		// Style elements can pull remote resources through @import and url(), and there is no
		// attribute to strip — the request lives in the text content.
		if (!allowRemote && node.nodeName === "STYLE") {
			node.textContent = "";
		}
	});

	DOMPurify.addHook("uponSanitizeAttribute", (node, data) => {
		if (allowRemote) return;

		if (data.attrName === "style" && data.attrValue.includes("url(")) {
			blockedRemoteCount++;
			data.keepAttr = false;
			return;
		}

		if (!fetchingAttributes.includes(data.attrName)) return;

		// cid: is not remote: it names a part of this message, which is already downloaded.
		// It survives sanitisation and is rewritten to a blob URL afterwards — and until then
		// it can load nothing, because the frame's policy has no cid: source at all. An
		// earlier version stripped it here, which left every inline image broken while every
		// unit test still passed.
		if (
			data.attrValue.startsWith("blob:") ||
			data.attrValue.startsWith("data:") ||
			data.attrValue.startsWith("cid:")
		)
			return;

		blockedRemoteCount++;
		data.keepAttr = false;
	});

	try {
		const sanitised = DOMPurify.sanitize(html, {
			// Scripts, forms and frames are removed outright (§13). A form in a message body
			// exists to collect credentials, and there is no legitimate use for one in mail.
			FORBID_TAGS: [
				"script",
				"form",
				"iframe",
				"object",
				"embed",
				"base",
				"meta",
			],
			FORBID_ATTR: ["formaction", "ping", "target"],
			ALLOW_DATA_ATTR: false,
			ALLOWED_URI_REGEXP: allowedSchemes,
		});

		return { html: sanitised, blockedRemoteCount };
	} finally {
		DOMPurify.removeAllHooks();
	}
}

/**
 * Rewrites `cid:` references to blob URLs the isolated document can load.
 *
 * Returns the URLs it created so the caller can revoke them: a blob URL holds its data alive
 * for the lifetime of the document, and a reading pane that never revoked them would grow
 * with every message opened.
 */
export async function resolveInlineImages(
	html: string,
	messageId: string,
	fetchPart: (messageId: string, contentId: string) => Promise<Blob>,
): Promise<{ html: string; revoke: () => void }> {
	const references = [...html.matchAll(/["']cid:([^"']+)["']/gi)];
	const created: string[] = [];
	let rewritten = html;

	for (const [, contentId] of references) {
		try {
			const blob = await fetchPart(messageId, contentId);
			const url = URL.createObjectURL(blob);
			created.push(url);
			rewritten = rewritten.replaceAll(`cid:${contentId}`, url);
		} catch (error) {
			// A part the message references but does not contain. Leaving the cid: in place is
			// harmless — nothing can load it — and a broken image is a truer rendering than
			// silently removing something the sender put there. Logged because the same
			// symptom appears when the fetch itself is broken, and a silent catch made that
			// indistinguishable.
			console.warn(
				`inline part ${contentId} could not be loaded: ${String(error)}`,
			);
		}
	}

	return {
		html: rewritten,
		revoke: () => created.forEach((url) => URL.revokeObjectURL(url)),
	};
}
