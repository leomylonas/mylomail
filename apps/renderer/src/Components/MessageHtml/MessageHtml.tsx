import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, ComboButton, MenuItem, useTheme } from "@carbon/react";
import { ViewOff } from "@carbon/icons-react";
import { applyMessageStyles } from "@mylomail/renderer/Components/MessageHtml/ApplyMessageStyles";
import { darkModeStyle } from "@mylomail/renderer/Components/MessageHtml/DarkModeStyle";
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

const printStyle =
	"@media print { html, body { margin: 0 !important; background: #fff !important; color: #000 !important; } img { max-width: 100% !important; height: auto !important; } }";

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
	invertColours = false,
}: {
	html: string;
	messageId: string;
	/** For the persisted remote-content allow list (§13 Epic 5), passed down from ReadingPane. */
	senderAddress?: string;
	/** Tracks whether this exact message document is loaded and sized for printing. */
	onReadyChange?: (ready: boolean) => void;
	/** A viewing mode the reader switched on for this message; never stored anywhere. */
	invertColours?: boolean;
}) {
	const remoteContentDecision = useRemoteContentDecision(senderAddress);
	const putRemoteContentRules = usePutRemoteContentRules();
	const [allowRemoteOverride, setAllowRemoteOverride] = useState(false);
	const allowRemote =
		remoteContentDecision === "allow" ||
		(remoteContentDecision !== "block" && allowRemoteOverride);
	const senderDomain = senderAddress?.split("@").at(-1) ?? "";
	// Loading is always for this message; a rule is saved only when asked for.
	const loadContent = (rules: RuleInput[]) => {
		setAllowRemoteOverride(true);
		if (rules.length > 0) putRemoteContentRules.mutate(rules);
	};
	const [inlineResolution, setInlineResolution] = useState<InlineResolution>({
		messageId: "",
		revision: "",
		html: null,
		status: "resolving",
	});
	const [loadedDocument, setLoadedDocument] = useState<string | null>(null);
	const printHostRef = useRef<HTMLDivElement>(null);
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
	const dark = useTheme().theme === "g100";
	const document = `<!doctype html><html><head><meta charset="utf-8"><meta http-equiv="Content-Security-Policy" content="${framePolicy(
		allowRemote,
	)}"></head><body>${renderedHtml}</body></html>`;
	const extraCss = `${darkModeStyle(renderedHtml, dark, invertColours)}${printStyle}`;
	const documentRevision = `${messageId}\u0000${dark}\u0000${invertColours}\u0000${document}`;
	const ready =
		(currentResolution.status === "resolved" ||
			currentResolution.status === "failed") &&
		loadedDocument === documentRevision;
	useEffect(() => {
		onReadyChange?.(ready);
	}, [documentRevision, onReadyChange, ready]);

	// Paper cannot show a scrolling frame: a replaced element is never split across pages, so an
	// iframe taller than a page is clipped to one and its content is lost. For printing the same
	// sanitised markup is also laid out in a shadow root — in the normal page flow, so it
	// paginates — and the shadow boundary keeps the message's own CSS away from the app.
	// Prepared ahead of time, hidden on screen, so its images are loaded when printing begins.
	const populatedFor = useRef<string | null>(null);
	const populatePrintCopy = useCallback(() => {
		const host = printHostRef.current;
		if (!host || populatedFor.current === renderedHtml) return;
		const root = host.shadowRoot ?? host.attachShadow({ mode: "open" });
		root.innerHTML = renderedHtml;
		applyMessageStyles(
			root,
			":host { display: block; background: #fff; color: #000; } img { max-width: 100%; height: auto; }",
		);
		populatedFor.current = renderedHtml;
	}, [renderedHtml]);
	useEffect(() => {
		// While remote content is blocked the markup holds none, so the copy can be prepared now
		// and its images are ready when printing starts. Once remote content is allowed, copying
		// early would fetch every remote image a second time, from the app's own page rather than
		// the locked-down frame, so it waits for printing itself.
		if (!allowRemote) populatePrintCopy();
		const printing = window.matchMedia("print");
		const onPrint = () => {
			if (printing.matches) populatePrintCopy();
		};
		printing.addEventListener("change", onPrint);
		window.addEventListener("beforeprint", populatePrintCopy);
		return () => {
			printing.removeEventListener("change", onPrint);
			window.removeEventListener("beforeprint", populatePrintCopy);
		};
	}, [allowRemote, populatePrintCopy]);

	return (
		<div className={styles.root} data-inline-status={currentResolution.status}>
			{prepared.blockedRemoteCount > 0 &&
			!allowRemote &&
			remoteContentDecision === "block" ? (
				<div className={styles.notice}>
					<ViewOff size={16} className={styles.noticeIcon} aria-hidden="true" />
					<p className={styles.noticeText}>
						Remote content is blocked by your sender or domain policy. Change
						the rule in Settings to load it.
					</p>
				</div>
			) : prepared.blockedRemoteCount > 0 && !allowRemote ? (
				<div className={styles.notice}>
					<ViewOff size={16} className={styles.noticeIcon} aria-hidden="true" />
					<p className={styles.noticeText}>
						Remote content is blocked. Loading it tells the sender you opened
						this message.
					</p>
					{senderAddress ? (
						<ComboButton
							className={styles.noticeAction}
							label="Load content"
							size="sm"
							tooltipAlignment="top-end"
							onClick={() => loadContent([])}
						>
							<MenuItem
								label="Trust sender"
								onClick={() =>
									loadContent([
										{
											scope: RemoteContentRuleScope.Sender,
											decision: RemoteContentRuleDecision.Allow,
											value: senderAddress,
										},
									])
								}
							/>
							<MenuItem
								label="Trust domain"
								onClick={() =>
									loadContent([
										{
											scope: RemoteContentRuleScope.Domain,
											decision: RemoteContentRuleDecision.Allow,
											value: senderDomain,
										},
									])
								}
							/>
						</ComboButton>
					) : (
						<Button
							className={styles.noticeAction}
							size="sm"
							kind="tertiary"
							onClick={() => loadContent([])}
						>
							Load content
						</Button>
					)}
				</div>
			) : null}
			<div ref={printHostRef} className={styles.printHost} aria-hidden="true" />
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
					const frameDocument = frameRef.current?.contentDocument;
					frameDocument?.addEventListener(
						"click",
						(event) => {
							const anchor = (event.target as Element | null)?.closest(
								"a[href]",
							);
							if (!anchor) return;
							event.preventDefault();
							const url = new URL(
								anchor.getAttribute("href") ?? "",
								window.location.origin,
							);
							if (url.protocol === "http:" || url.protocol === "https:") {
								window.open(url.toString(), "_blank", "noopener");
							}
						},
						true,
					);
					if (frameDocument) applyMessageStyles(frameDocument, extraCss);
					setLoadedDocument(documentRevision);
				}}
			/>
		</div>
	);
}

/** Fetches one MIME part. Same-origin, so the launch cookie authenticates it (§9). */
async function fetchPart(messageId: string, contentId: string): Promise<Blob> {
	const response = await fetchApi(`/messages/${messageId}/parts`, {
		method: "POST",
		headers: { "Content-Type": "application/json" },
		body: JSON.stringify({ contentId }),
	});
	// fetchApi preserves the server's not-found detail and category for the caller.
	return response.blob();
}
