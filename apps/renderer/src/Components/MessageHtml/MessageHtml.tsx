import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Checkbox } from "@carbon/react";
import {
	prepare,
	resolveInlineImages,
} from "@mylomail/renderer/Components/MessageHtml/SanitiseMessageHtml";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/MessageHtml/MessageHtml.module.css";

interface TrustedSender {
	address: string;
}

const trustedSendersKey = ["remote-content-trusted-senders"];

/**
 * Whether remote content should load without asking, because this sender is on the
 * persisted allow list (§13 Epic 5) — checked fresh per message rather than cached forever,
 * since trusting/untrusting a sender should show up the next time any of their mail opens.
 */
function useIsTrustedSender(senderAddress: string | undefined): boolean {
	const query = useQuery({
		queryKey: trustedSendersKey,
		queryFn: async (): Promise<TrustedSender[]> => {
			const response = await fetch("/remote-content/trusted-senders");
			if (!response.ok) return [];
			return (await response.json()) as TrustedSender[];
		},
		staleTime: 30_000,
	});

	if (!senderAddress) return false;
	const lowered = senderAddress.toLowerCase();
	return query.data?.some((sender) => sender.address === lowered) ?? false;
}

function useTrustSender() {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	return useMutation({
		mutationFn: async (address: string) => {
			const response = await fetch("/remote-content/trusted-senders", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ address }),
			});
			if (!response.ok)
				throw new Error(`Could not trust ${address} (${response.status}).`);
		},
		onSuccess: () => {
			void queryClient.invalidateQueries({ queryKey: trustedSendersKey });
		},
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The sender could not be trusted",
				detail: error instanceof Error ? error.message : String(error),
			}),
	});
}

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
	senderAddress,
}: {
	html: string;
	messageId: string;
	/** For the persisted remote-content allow list (§13 Epic 5), passed down from ReadingPane. */
	senderAddress?: string;
}) {
	const isTrustedSender = useIsTrustedSender(senderAddress);
	const trustSender = useTrustSender();
	// A one-shot manual override ("Load content" clicked this session) OR'd with the
	// allow-list check, rather than seeded via an effect: the allow-list query resolving after
	// mount just changes what this expression evaluates to on the next render, with no extra
	// state or cascading setState needed.
	const [allowRemoteOverride, setAllowRemoteOverride] = useState(false);
	const allowRemote = allowRemoteOverride || isTrustedSender;
	const [alwaysAllow, setAlwaysAllow] = useState(false);
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
					{senderAddress ? (
						<Checkbox
							id="message-html-always-allow"
							labelText={`Always allow images from ${senderAddress}`}
							checked={alwaysAllow}
							onChange={(_, { checked }) => setAlwaysAllow(checked)}
						/>
					) : null}
					<Button
						size="sm"
						kind="tertiary"
						onClick={() => {
							setAllowRemoteOverride(true);
							if (alwaysAllow && senderAddress) {
								trustSender.mutate(senderAddress);
							}
						}}
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
