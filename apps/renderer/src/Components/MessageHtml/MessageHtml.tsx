import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Checkbox } from "@carbon/react";
import type { RemoteContentRuleDto } from "@mylomail/shared-types/Api/Contracts/RemoteContentRuleDto";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";
import { decideRemoteContent } from "@mylomail/renderer/Components/MessageHtml/RemoteContentPolicy";
import {
	prepare,
	resolveInlineImages,
} from "@mylomail/renderer/Components/MessageHtml/SanitiseMessageHtml";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/MessageHtml/MessageHtml.module.css";

const remoteContentRulesKey = ["remote-content-rules"];

function useRemoteContentDecision(senderAddress: string | undefined) {
	const query = useQuery({
		queryKey: remoteContentRulesKey,
		queryFn: async (): Promise<RemoteContentRuleDto[]> => {
			const response = await fetch("/remote-content/rules");
			if (!response.ok) return [];
			return (await response.json()) as RemoteContentRuleDto[];
		},
		staleTime: 30_000,
	});

	return decideRemoteContent(query.data ?? [], senderAddress);
}

interface RuleInput {
	scope: RemoteContentRuleScope;
	decision: RemoteContentRuleDecision;
	value: string;
}

function usePutRemoteContentRules() {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	return useMutation({
		mutationFn: async (rules: RuleInput[]) => {
			await Promise.all(
				rules.map(async (rule) => {
					const response = await fetch("/remote-content/rules", {
						method: "PUT",
						headers: { "Content-Type": "application/json" },
						body: JSON.stringify(rule),
					});
					if (!response.ok)
						throw new Error(
							`Could not save the remote-content rule (${response.status}).`,
						);
				}),
			);
		},
		onSuccess: () => {
			void queryClient.invalidateQueries({ queryKey: remoteContentRulesKey });
		},
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The remote-content rule could not be saved",
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
	const remoteContentDecision = useRemoteContentDecision(senderAddress);
	const putRemoteContentRules = usePutRemoteContentRules();
	const [allowRemoteOverride, setAllowRemoteOverride] = useState(false);
	const allowRemote =
		remoteContentDecision === "allow" ||
		(remoteContentDecision !== "block" && allowRemoteOverride);
	const [alwaysAllowSender, setAlwaysAllowSender] = useState(false);
	const [alwaysAllowDomain, setAlwaysAllowDomain] = useState(false);
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
			{prepared.blockedRemoteCount > 0 &&
			!allowRemote &&
			remoteContentDecision === "block" ? (
				<div className={styles.notice}>
					<span>
						Remote content is blocked by your sender or domain policy. Change
						the rule in Settings to load it.
					</span>
				</div>
			) : prepared.blockedRemoteCount > 0 && !allowRemote ? (
				<div className={styles.notice}>
					<span>
						Remote content is blocked. Loading it tells the sender you opened
						this message.
					</span>
					{senderAddress ? (
						<>
							<Checkbox
								id="message-html-always-allow-sender"
								labelText={`Always allow images from ${senderAddress}`}
								checked={alwaysAllowSender}
								onChange={(_, { checked }) => setAlwaysAllowSender(checked)}
							/>
							<Checkbox
								id="message-html-always-allow-domain"
								labelText={`Always allow images from ${senderAddress.split("@").at(-1)}`}
								checked={alwaysAllowDomain}
								onChange={(_, { checked }) => setAlwaysAllowDomain(checked)}
							/>
						</>
					) : null}
					<Button
						size="sm"
						kind="tertiary"
						onClick={() => {
							setAllowRemoteOverride(true);
							if (!senderAddress) return;
							const rules: RuleInput[] = [];
							if (alwaysAllowSender) {
								rules.push({
									scope: RemoteContentRuleScope.Sender,
									decision: RemoteContentRuleDecision.Allow,
									value: senderAddress,
								});
							}
							if (alwaysAllowDomain) {
								rules.push({
									scope: RemoteContentRuleScope.Domain,
									decision: RemoteContentRuleDecision.Allow,
									value: senderAddress.split("@").at(-1) ?? "",
								});
							}
							if (rules.length > 0) putRemoteContentRules.mutate(rules);
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
				// Same-origin is required for the authenticated renderer's blob URLs. Scripts,
				// forms, frames and navigation remain independently forbidden, so message HTML
				// still has no executable path to the renderer's mail capabilities (§13).
				sandbox="allow-same-origin"
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
