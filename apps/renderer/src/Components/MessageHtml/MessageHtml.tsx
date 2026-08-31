import { useEffect, useMemo, useState } from "react";
import { Button } from "@carbon/react";
import {
	prepare,
	resolveInlineImages,
} from "@mylomail/renderer/Components/MessageHtml/SanitiseMessageHtml";
import styles from "@mylomail/renderer/Components/MessageHtml/MessageHtml.module.css";

/**
 * The document policy applied inside the isolated frame.
 *
 * <b>This is the request-layer enforcement §13 requires.</b> Stripping attributes is a parser
 * competing with an attacker; a content policy is the browser refusing to make the request at
 * all, and it covers the routes stripping misses — CSS `url()`, `@import`, `srcset`, SVG
 * references. `default-src 'none'` means anything not named here simply cannot load.
 */
const framePolicy = (allowRemote: boolean) =>
	[
		"default-src 'none'",
		// Inline images arrive as blobs, fetched by authenticated code. Remote images load
		// only once the user has asked for them.
		`img-src blob: data:${allowRemote ? " https: http:" : ""}`,
		"style-src 'unsafe-inline'",
		"font-src data:",
		// No script, ever. Not even from this origin: a message is not code.
		"script-src 'none'",
		"form-action 'none'",
		"base-uri 'none'",
		"frame-src 'none'",
	].join("; ");

export function MessageHtml({
	html,
	messageId,
}: {
	html: string;
	messageId: string;
}) {
	const [allowRemote, setAllowRemote] = useState(false);
	const [resolved, setResolved] = useState<string | null>(null);
	const [inlineStatus, setInlineStatus] = useState<
		"idle" | "resolving" | "resolved" | "failed"
	>("idle");

	const prepared = useMemo(
		() => prepare(html, allowRemote),
		[html, allowRemote],
	);

	useEffect(() => {
		let revoke = () => undefined as void;
		let cancelled = false;
		setInlineStatus("resolving");

		void resolveInlineImages(prepared.html, messageId, fetchPart).then(
			(result) => {
				if (cancelled) {
					result.revoke();
					return;
				}

				revoke = result.revoke;
				setResolved(result.html);
				setInlineStatus("resolved");
			},
			() => {
				if (!cancelled) setInlineStatus("failed");
			},
		);

		return () => {
			cancelled = true;
			// Blob URLs keep their data alive for the document's lifetime, so a pane that
			// never revoked them would grow with every message opened.
			revoke();
		};
	}, [prepared.html, messageId]);

	const document = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${framePolicy(
		allowRemote,
	)}"></head><body>${resolved ?? prepared.html}</body></html>`;

	return (
		<div data-inline-status={inlineStatus}>
			{prepared.blockedRemoteCount > 0 && !allowRemote ? (
				<div className={styles.notice}>
					<span>
						Remote content is blocked. Loading it tells the sender you opened
						this message.
					</span>
					<Button
						size="sm"
						kind="tertiary"
						onClick={() => setAllowRemote(true)}
					>
						Load content
					</Button>
				</div>
			) : null}
			<iframe
				key={document}
				className={styles.frame}
				title="Message body"
				// An opaque origin with no scripts and no same-origin access: even if
				// sanitisation missed something, it runs with no capability to reach the
				// renderer that can mutate mail (§13).
				sandbox=""
				srcDoc={document}
			/>
		</div>
	);
}

/** Fetches one MIME part. Same-origin, so the launch cookie authenticates it (§9). */
async function fetchPart(messageId: string, contentId: string): Promise<Blob> {
	const url = `/messages/${messageId}/parts/${encodeURIComponent(contentId)}`;
	const response = await fetch(url);
	// The URL is in the message because "404" alone cannot distinguish a part the message
	// does not contain from a path this code built wrongly.
	if (!response.ok) throw new Error(`${url} responded ${response.status}`);
	return response.blob();
}
