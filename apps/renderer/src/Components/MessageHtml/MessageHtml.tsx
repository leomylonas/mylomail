import { useCallback, useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, ComboButton, MenuItem, useTheme } from "@carbon/react";
import { ViewOff } from "@carbon/icons-react";
import { fillShadowRoot } from "@mylomail/renderer/Components/MessageHtml/ApplyMessageStyles";
import { MessageHtmlInfoDtoSchema } from "@mylomail/shared-types/Api/Contracts/MessageHtmlInfoDto";
import { PrintHtmlDtoSchema } from "@mylomail/shared-types/Api/Contracts/PrintHtmlDto";
import { RemoteContentDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentDecision";
import { RemoteContentRuleDecision } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleDecision";
import { RemoteContentRuleScope } from "@mylomail/shared-types/Api/Domain/RemoteContentRuleScope";
import {
	fetchApi,
	fetchApiJson,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/MessageHtml/MessageHtml.module.css";

/** What the backend prepared, per message and per "load remote content" request for this view. */
export const messageHtmlKey = ["message-html"];

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
			// The backend applies the rules when it prepares a message, so every prepared message and
			// the rules list are stale.
			void queryClient.invalidateQueries({
				queryKey: ["remote-content-rules"],
			});
			void queryClient.invalidateQueries({ queryKey: messageHtmlKey });
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
 * A message body shown in an isolated frame (§13).
 *
 * The backend prepares it — sanitises it, withholds remote content according to the persisted rules
 * and this view's own request, embeds inline images — and serves the result as a document under a
 * content policy of its own. This component never handles untrusted markup except to place the
 * backend's already-sanitised copy in the print layout.
 */
export function MessageHtml({
	messageId,
	senderAddress,
	onReadyChange,
	invertColours = false,
}: {
	messageId: string;
	/** For the persisted remote-content allow list (§13 Epic 5), passed down from ReadingPane. */
	senderAddress?: string;
	/** Tracks whether this exact message document is loaded and sized for printing. */
	onReadyChange?: (ready: boolean) => void;
	/** A viewing mode the reader switched on for this message; never stored anywhere. */
	invertColours?: boolean;
}) {
	const putRemoteContentRules = usePutRemoteContentRules();
	// Local to this view: the reader asked to load this message's remote content.
	const [allowRemoteOverride, setAllowRemoteOverride] = useState(false);
	const prepared = useQuery({
		queryKey: [...messageHtmlKey, "info", messageId, allowRemoteOverride],
		queryFn: () =>
			fetchApiJson(
				`/messages/${messageId}/html-info?remote=${allowRemoteOverride}`,
				MessageHtmlInfoDtoSchema,
			),
	});
	const allowRemote = prepared.data?.remoteAllowed ?? false;
	const blockedRemoteCount = prepared.data?.blockedRemoteCount ?? 0;
	const remoteContentDecision = prepared.data?.decision;
	const senderDomain = senderAddress?.split("@").at(-1) ?? "";
	// Loading is always for this message; a rule is saved only when asked for.
	const loadContent = (rules: RuleInput[]) => {
		setAllowRemoteOverride(true);
		if (rules.length > 0) putRemoteContentRules.mutate(rules);
	};
	const [loadedFrame, setLoadedFrame] = useState<string | null>(null);
	const printHostRef = useRef<HTMLDivElement>(null);
	const frameRef = useRef<HTMLIFrameElement>(null);

	const dark = useTheme().theme === "g100";
	// Everything that changes what the document says is in the URL, so a change reloads the frame
	// and the loaded document is known by its URL alone. `allowed` is whether remote content actually
	// loads (a rule can decide it): a rule saved while the reader had already asked to load changes
	// nothing in the document, so it must not reload it and fetch every remote image again.
	const frameUrl = prepared.data
		? `/messages/${messageId}/frame?remote=${allowRemoteOverride}&dark=${dark}&invert=${invertColours}&allowed=${prepared.data.remoteAllowed}`
		: null;
	const ready = frameUrl !== null && loadedFrame === frameUrl;
	useEffect(() => {
		onReadyChange?.(ready);
	}, [frameUrl, onReadyChange, ready]);

	// Paper cannot show a scrolling frame: a replaced element is never split across pages, so an
	// iframe taller than a page is clipped to one and its content is lost. For printing the same
	// prepared markup is also laid out in a shadow root — in the normal page flow, so it paginates —
	// and the shadow boundary keeps the message's own CSS away from the app. Fetched once the frame
	// is up and kept hidden on screen, so its images are loaded when printing begins.
	const printCopy = useQuery({
		queryKey: [...messageHtmlKey, "print", messageId, allowRemoteOverride],
		queryFn: () =>
			fetchApiJson(
				`/messages/${messageId}/print-html?remote=${allowRemoteOverride}`,
				PrintHtmlDtoSchema,
			),
		enabled: ready,
	});
	const printData = printCopy.data;
	const populatedFor = useRef<unknown>(null);
	const populatePrintCopy = useCallback(() => {
		const host = printHostRef.current;
		if (!host || !printData || populatedFor.current === printData) return;
		fillShadowRoot(
			host.shadowRoot ?? host.attachShadow({ mode: "open" }),
			printData,
			":host { display: block; background: #fff; color: #000; } img { max-width: 100%; height: auto; }",
		);
		populatedFor.current = printData;
	}, [printData]);
	useEffect(() => {
		// While remote content is blocked the markup holds none, so the copy can be laid out now
		// and its images are ready when printing starts. Once remote content is allowed, laying it
		// out early would fetch every remote image a second time, from the app's own page rather than
		// the locked-down frame, so it waits for printing itself. The data is already here by then.
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
		<div className={styles.root}>
			{blockedRemoteCount > 0 &&
			!allowRemote &&
			remoteContentDecision === RemoteContentDecision.Block ? (
				<div className={styles.notice}>
					<ViewOff size={16} className={styles.noticeIcon} aria-hidden="true" />
					<p className={styles.noticeText}>
						Remote content is blocked by your sender or domain policy. Change
						the rule in Settings to load it.
					</p>
				</div>
			) : blockedRemoteCount > 0 && !allowRemote ? (
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
			{frameUrl ? (
				<iframe
					ref={frameRef}
					key={frameUrl}
					className={styles.frame}
					title="Message body"
					// Same-origin keeps the reader's click handling below able to reach the document.
					// Scripts, forms, frames and navigation remain independently forbidden — by this
					// sandbox and by the policy the backend serves the document under — so message HTML
					// still has no executable path to the renderer's mail capabilities (§13).
					sandbox="allow-same-origin"
					src={frameUrl}
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
						setLoadedFrame(frameUrl);
					}}
				/>
			) : null}
		</div>
	);
}
