import { useQuery } from "@tanstack/react-query";
import { ReadingPane } from "@mylomail/renderer/Components/ReadingPane/ReadingPane";
import {
	buildForwardSeed,
	buildReplySeed,
	copyForwardAttachments,
	resolveOriginalHtml,
	type ForwardAttachment,
	type MessageReplyContext,
} from "@mylomail/renderer/Components/Compose/ComposeReplyForward";
import { useHub } from "@mylomail/renderer/Shell/Backend/UseHub";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Shell/Windows/StandaloneWindow.module.css";

interface Account {
	id: string;
	emailAddress: string;
}

/**
 * A single message, opened in its own window (§13 Epic 10).
 *
 * Its own hub connection, its own query cache — the same per-window rule every window follows
 * (`docs/skills/frontend-shell.md`), just with nothing else in the document beside it.
 */
export function MessageWindow({
	messageId,
	subject,
	senderAddress,
}: {
	messageId: string;
	subject: string;
	/**
	 * The message's first `From` address, carried over the query string from the window that
	 * popped this one out (§13 Epic 5/10) — without it, a sender already on the persisted
	 * remote-content allow list would still be blocked and re-prompted here, contradicting
	 * that allow list's whole point of not asking twice.
	 */
	senderAddress?: string;
}) {
	const { hub, status } = useHub();
	const { store: notifications } = useWindowNotifications();
	const accounts = useQuery({
		queryKey: ["accounts"],
		queryFn: async (): Promise<Account[]> => {
			const response = await fetch("/accounts");
			if (!response.ok)
				throw new Error(`accounts responded ${response.status}`);
			return (await response.json()) as Account[];
		},
	});

	/**
	 * Reply/reply-all/forward has no inline `Compose` mounted here to seed — unlike the main
	 * window, which opens a reply into its own compose pane and lets that pane's own autosave
	 * create the draft — so this creates the draft up front via `SaveDraft`, then hands off to
	 * a genuinely new, independent compose window the same way detaching an existing draft
	 * already does (§13 Epic 10). No `?message=` window ever becomes a `?compose=` one in place.
	 */
	const onReply = async (
		mode: "reply" | "replyAll" | "forward",
	): Promise<void> => {
		if (!hub) return;
		try {
			const [context, body] = await Promise.all([
				hub.invoke<MessageReplyContext>("GetMessageReplyContext", messageId),
				hub.invoke<{
					html: string | null;
					text: string | null;
					isFetched: boolean;
					isFailed: boolean;
				}>("GetMessageBody", messageId),
			]);
			const account = accounts.data?.find((a) => a.id === context.accountId);
			const originalHtml = resolveOriginalHtml(body);
			const seed =
				mode === "forward"
					? buildForwardSeed(
							context,
							originalHtml,
							await hub.invoke<ForwardAttachment[]>(
								"GetAttachmentMetadata",
								messageId,
							),
						)
					: buildReplySeed(
							mode,
							context,
							originalHtml,
							account?.emailAddress ?? "",
						);

			const saved = await hub.invoke<{ id: string }>("SaveDraft", {
				accountId: context.accountId,
				inReplyToMessageId: seed.inReplyToMessageId,
				to: seed.to,
				cc: seed.cc,
				bcc: seed.bcc,
				subject: seed.subject,
				bodyHtml: seed.bodyHtml,
			});

			if (seed.forwardAttachments) {
				const failed = await copyForwardAttachments(
					saved.id,
					seed.forwardAttachments,
				);
				if (failed.length > 0) {
					notify(notifications, {
						kind: "error",
						title: "Some attachments could not be copied",
						detail: `Couldn't copy ${failed.length === 1 ? "this attachment" : "these attachments"} from the original message: ${failed.join(", ")}.`,
					});
				}
			}

			try {
				await window.windows?.open(
					`compose=${saved.id}&account=${context.accountId}`,
				);
			} catch (error) {
				// The draft already exists on the server at this point, but no window will ever
				// show it to the user — left as-is, a retry would create a second orphan on top
				// of this one, and this one would sit invisible in Drafts forever. Discarding it
				// keeps a failed "reply" from silently leaving debris behind.
				await hub.invoke("DeleteDraft", saved.id).catch(() => undefined);
				throw error;
			}
		} catch {
			notify(notifications, {
				kind: "error",
				title: "Could not start this reply",
				detail: "Try again from the main window instead.",
			});
		}
	};

	return (
		<div className={styles.window}>
			{hub ? (
				<ReadingPane
					hub={hub}
					messageId={messageId}
					subject={subject}
					senderAddress={senderAddress}
					onReply={window.windows ? (mode) => void onReply(mode) : undefined}
				/>
			) : (
				<p className={styles.status}>
					{status === "failed"
						? "Disconnected from the backend."
						: "Connecting…"}
				</p>
			)}
		</div>
	);
}
