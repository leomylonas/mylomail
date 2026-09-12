import { useCallback, useEffect, useMemo, useRef, useState } from "react";
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
import {
	fetchApi,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/MessageHtml/MessageHtml.module.css";

const remoteContentRulesKey = ["remote-content-rules"];

function useRemoteContentDecision(senderAddress: string | undefined) {
	const query = useQuery({
		queryKey: remoteContentRulesKey,
		queryFn: async (): Promise<RemoteContentRuleDto[]> => {
			const response = await fetchApi("/remote-content/rules");
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
					await fetchApi("/remote-content/rules", {
						method: "PUT",
						headers: { "Content-Type": "application/json" },
						body: JSON.stringify(rule),
					});
				}),
			);
		},
		onSuccess: () => {
			void queryClient.invalidateQueries({ queryKey: remoteContentRulesKey });
		},
		onError: (error: unknown) =>
			notify(
				notifications,
				notificationForError(
					error,
					"The remote-content rule could not be saved",
				),
			),
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

type InlineResolution = {
	messageId: string;
	revision: string;
	html: string | null;
	status: "resolving" | "resolved" | "failed";
};

export function MessageHtml({
	html,
	messageId,
	senderAddress,
	onReadyChange,
}: {
	html: string;
	messageId: string;
	/** For the persisted remote-content allow list (§13 Epic 5), passed down from ReadingPane. */
	senderAddress?: string;
	/** Tracks whether this exact message document is loaded and sized for printing. */
	onReadyChange?: (ready: boolean) => void;
}) {
	const remoteContentDecision = useRemoteContentDecision(senderAddress);
	const putRemoteContentRules = usePutRemoteContentRules();
	const [allowRemoteOverride, setAllowRemoteOverride] = useState(false);
	const allowRemote =
		remoteContentDecision === "allow" ||
		(remoteContentDecision !== "block" && allowRemoteOverride);
	const [alwaysAllowSender, setAlwaysAllowSender] = useState(false);
	const [alwaysAllowDomain, setAlwaysAllowDomain] = useState(false);
	const [inlineResolution, setInlineResolution] = useState<InlineResolution>({
		messageId: "",
		revision: "",
		html: null,
		status: "resolving",
	});
	const [loadedDocument, setLoadedDocument] = useState<string | null>(null);
	const frameRef = useRef<HTMLIFrameElement>(null);

	const prepared = useMemo(
		() => prepare(html, allowRemote),
		[html, allowRemote],
	);

	const sourceRevision = `${messageId}\u0000${prepared.html}`;
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
				setInlineResolution({
					messageId,
					revision: sourceRevision,
					html: result.html,
					status: "resolved",
				});
			},
			() => {
				if (!cancelled) {
					setInlineResolution({
						messageId,
						revision: sourceRevision,
						html: null,
						status: "failed",
					});
				}
			},
		);

		return () => {
			cancelled = true;
			// Blob URLs keep their data alive for the document's lifetime, so a pane that
			// never revoked them would grow with every message opened.
			revoke();
		};
	}, [messageId, prepared.html, sourceRevision]);

	const currentResolution =
		inlineResolution.revision === sourceRevision
			? inlineResolution
			: {
					messageId,
					revision: sourceRevision,
					html: null,
					status: "resolving" as const,
				};
	const renderedHtml =
		currentResolution.html ??
		(inlineResolution.messageId === messageId ? inlineResolution.html : null) ??
		prepared.html;
	const document = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${framePolicy(
		allowRemote,
	)}"></head><body>${renderedHtml}<style>@media print { html, body { margin: 0 !important; background: #fff !important; color: #000 !important; } img { max-width: 100% !important; height: auto !important; } }</style></body></html>`;
	const documentRevision = `${messageId}\u0000${document}`;
	const ready =
		(currentResolution.status === "resolved" ||
			currentResolution.status === "failed") &&
		loadedDocument === documentRevision;
	useEffect(() => {
		onReadyChange?.(ready);
	}, [documentRevision, onReadyChange, ready]);

	const resizeFrame = useCallback(() => {
		const frame = frameRef.current;
		const frameDocument = frame?.contentDocument;
		if (!frame || !frameDocument) return;

		// `beforeprint` runs after print media has been selected. Re-measuring there uses
		// the paper-width layout rather than the wider on-screen pane, so reflowed content
		// cannot be clipped at the iframe's old screen height.
		frame.height = "1";
		const height = Math.max(
			frameDocument.documentElement.scrollHeight,
			frameDocument.body?.scrollHeight ?? 0,
		);
		if (height > 0) frame.height = String(height);
	}, []);

	useEffect(() => {
		window.addEventListener("beforeprint", resizeFrame);
		window.addEventListener("afterprint", resizeFrame);
		return () => {
			window.removeEventListener("beforeprint", resizeFrame);
			window.removeEventListener("afterprint", resizeFrame);
		};
	}, [resizeFrame]);

	return (
		<div data-inline-status={currentResolution.status}>
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
				ref={frameRef}
				key={documentRevision}
				className={styles.frame}
				title="Message body"
				// Same-origin is required for the authenticated renderer's blob URLs. Scripts,
				// forms, frames and navigation remain independently forbidden, so message HTML
				// still has no executable path to the renderer's mail capabilities (§13).
				sandbox="allow-same-origin"
				srcDoc={document}
				onLoad={() => {
					resizeFrame();
					setLoadedDocument(documentRevision);
				}}
			/>
		</div>
	);
}

/** Fetches one MIME part. Same-origin, so the launch cookie authenticates it (§9). */
async function fetchPart(messageId: string, contentId: string): Promise<Blob> {
	const url = `/messages/${messageId}/parts/${encodeURIComponent(contentId)}`;
	const response = await fetchApi(url);
	// fetchApi preserves the server's not-found detail and category for the caller.
	return response.blob();
}
