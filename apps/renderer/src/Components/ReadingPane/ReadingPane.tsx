import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { ActionableNotification, Button, SkeletonText } from "@carbon/react";
import type { MessageInviteDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { InviteResponse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { MessageHtml } from "@mylomail/renderer/Components/MessageHtml/MessageHtml";
import { describeInviteWhen } from "@mylomail/renderer/Components/ReadingPane/InviteWhen";
import { AttachmentList } from "@mylomail/renderer/Components/AttachmentList/AttachmentList";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/ReadingPane/ReadingPane.module.css";

type MessageInvite = MessageInviteDto;

interface MessageBody {
	messageId: string;
	text: string | null;
	html: string | null;
	isFetched: boolean;
	isFailed: boolean;
}

/**
 * The selected message's body.
 *
 * Content arrives as background work rather than on open (§1), so "not fetched yet" is an
 * ordinary state with its own message — distinct from a message that genuinely has no body,
 * which would otherwise look identical and leave the user waiting for nothing.
 */
export function ReadingPane({
	hub,
	messageId,
	subject,
	senderAddress,
	onOpenInNewWindow,
	onReply,
}: {
	hub: HubConnection;
	messageId: string;
	subject: string;
	/**
	 * The message's first `From` address, for the remote-content allow list (§13 Epic 5).
	 * A popped-out message window has no message-list selection to read this from directly,
	 * so it is carried over that window's query string instead (see `MessageWindow`) — without
	 * it, an already-trusted sender's mail would be blocked and re-prompted there too, which
	 * would contradict the allow list's whole point. Still genuinely optional: absent only if
	 * the caller truly has no sender to offer, in which case the pane still works, just without
	 * the "always allow this sender" option.
	 */
	senderAddress?: string;
	/** Absent inside a window that is already just this one message (§13 Epic 10). */
	onOpenInNewWindow?: () => void;
	/**
	 * Absent in the main window, where the message list's own context menu already offers
	 * Reply/Reply all/Forward — present only for a popped-out message window (§13 Epic 10),
	 * which has no list row of its own to hang a context menu off, and would otherwise offer no
	 * way to reply at all short of switching back to the main window.
	 */
	onReply?: (mode: "reply" | "replyAll" | "forward") => void;
}) {
	const body = useQuery({
		queryKey: ["body", messageId],
		queryFn: () => hub.invoke<MessageBody>("GetMessageBody", messageId),
		// Content lands after the message does, so an unfetched body is worth asking about
		// again; a fetched one never changes unless its raw content is replaced. A failed one
		// never will, having already exhausted its retries server-side (§15) — polling it
		// forever would just be asking the same unanswerable question every two seconds. A
		// query error means the message itself is gone (deleted, or its account removed) —
		// also terminal, and also not worth asking again.
		refetchInterval: (query) =>
			query.state.data?.isFetched ||
			query.state.data?.isFailed ||
			query.state.error
				? false
				: 2000,
	});

	return (
		<article className={styles.pane} aria-label="Message">
			<div className={styles.subjectRow}>
				<h2 className={styles.subject}>{subject || "(no subject)"}</h2>
				{onReply ? (
					<>
						<Button size="sm" kind="ghost" onClick={() => onReply("reply")}>
							Reply
						</Button>
						<Button size="sm" kind="ghost" onClick={() => onReply("replyAll")}>
							Reply all
						</Button>
						<Button size="sm" kind="ghost" onClick={() => onReply("forward")}>
							Forward
						</Button>
					</>
				) : null}
				<Button size="sm" kind="ghost" onClick={() => window.print()}>
					Print
				</Button>
				{onOpenInNewWindow ? (
					<Button size="sm" kind="ghost" onClick={onOpenInNewWindow}>
						Open in new window
					</Button>
				) : null}
			</div>
			{body.isPending ? <SkeletonText paragraph lineCount={4} /> : null}
			{body.isError ? (
				<p className={styles.waiting} role="alert">
					This message is no longer available.
				</p>
			) : null}
			{body.data ? (
				<Body
					body={body.data}
					messageId={messageId}
					hub={hub}
					senderAddress={senderAddress}
				/>
			) : null}
		</article>
	);
}

function Body({
	body,
	messageId,
	hub,
	senderAddress,
}: {
	body: MessageBody;
	messageId: string;
	hub: HubConnection;
	senderAddress?: string;
}) {
	if (body.isFailed)
		return (
			<p className={styles.waiting} role="alert">
				Couldn&apos;t download this message. It may be temporarily unavailable
				from the server.
			</p>
		);

	if (!body.isFetched)
		return <p className={styles.waiting}>Downloading this message…</p>;

	// HTML preferred where both exist: it is what the sender composed, and the plain-text
	// alternative is usually a degraded copy of it.
	if (body.html)
		return (
			<>
				<InviteBanner hub={hub} messageId={messageId} />
				<MessageHtml
					// Remounts per message: the "load remote content" override is local state
					// that must never survive a message switch (§13 Epic 5) — a click on a
					// safe sender's message must not silently unblock trackers on the very
					// next, unrelated message this instance would otherwise carry over to.
					key={messageId}
					html={body.html}
					messageId={messageId}
					senderAddress={senderAddress}
				/>
				<AttachmentList hub={hub} messageId={messageId} />
			</>
		);

	if (body.text)
		return (
			<>
				<InviteBanner hub={hub} messageId={messageId} />
				<div className={styles.body}>{body.text}</div>
				<AttachmentList hub={hub} messageId={messageId} />
			</>
		);

	return (
		<>
			<InviteBanner hub={hub} messageId={messageId} />
			<p className={styles.waiting}>This message has no body.</p>
			<AttachmentList hub={hub} messageId={messageId} />
		</>
	);
}

const responseStatusLabel = [
	"",
	"accepted",
	"declined",
	"responded tentatively to",
];

/**
 * The reading pane's RSVP surface (§13 Epic 7) — the other half of Epic 7's Accept/Decline/
 * Tentative requirement, alongside the calendar view's own `EventModal`. Absent entirely for
 * an ordinary message: `GetMessageInvite` returns null for anything without a
 * `text/calendar; METHOD=REQUEST` part.
 */
function InviteBanner({
	hub,
	messageId,
}: {
	hub: HubConnection;
	messageId: string;
}) {
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const invite = useQuery({
		queryKey: ["invite", messageId],
		queryFn: () =>
			hub.invoke<MessageInvite | null>("GetMessageInvite", messageId),
	});

	const respond = useMutation({
		mutationFn: (response: InviteResponse) =>
			hub.invoke("RespondToInvite", invite.data?.eventId, response, null),
		onSuccess: () =>
			queryClient.invalidateQueries({ queryKey: ["invite", messageId] }),
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The response could not be sent",
				detail: error instanceof Error ? error.message : String(error),
			}),
	});

	const acceptUnverifiedReply = useMutation({
		mutationFn: () => hub.invoke("AcceptUnverifiedInviteReply", messageId),
		onSuccess: () => {
			void queryClient.invalidateQueries({ queryKey: ["invite", messageId] });
			void queryClient.invalidateQueries({ queryKey: ["calendar"] });
		},
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The claimed response was not applied",
				detail: error instanceof Error ? error.message : String(error),
			}),
	});

	if (!invite.data) return null;

	const when = describeInviteWhen(
		typeof invite.data.start === "string"
			? invite.data.start
			: invite.data.start.toISOString(),
		typeof invite.data.end === "string"
			? invite.data.end
			: invite.data.end.toISOString(),
		invite.data.isAllDay,
	);

	if (invite.data.isReply) {
		const claimedResponse = invite.data.claimedResponse;
		const claimed =
			claimedResponse == null
				? "sent a calendar reply"
				: `${responseStatusLabel[claimedResponse]} the invitation`;
		return (
			<div className={styles.invite}>
				<p className={styles.inviteTitle}>{invite.data.title}</p>
				<p className={styles.inviteWhen}>{when}</p>
				{invite.data.requiresManualReview ? (
					<ActionableNotification
						kind="warning"
						title="This calendar reply could not be authenticated"
						subtitle={`${invite.data.replyingAddress ?? "The sender"} claims they ${claimed}. Email headers can be forged. Apply this response only if you independently trust the sender and expected this reply.`}
						actionButtonLabel="Accept claimed response"
						onActionButtonClick={() => {
							if (!acceptUnverifiedReply.isPending) {
								acceptUnverifiedReply.mutate();
							}
						}}
						lowContrast
						hideCloseButton
						inline
					/>
				) : (
					<p>
						Verified calendar reply from{" "}
						{invite.data.replyingAddress ?? "the attendee"}.
					</p>
				)}
			</div>
		);
	}

	return (
		<div className={styles.invite}>
			<p className={styles.inviteTitle}>{invite.data.title}</p>
			<p className={styles.inviteWhen}>{when}</p>
			{invite.data.organizer ? (
				<p className={styles.inviteFrom}>
					{invite.data.organizer.name ?? invite.data.organizer.email} invited
					you
				</p>
			) : null}
			{invite.data.eventId ? (
				invite.data.myResponseStatus != null ? (
					<p>
						You {responseStatusLabel[invite.data.myResponseStatus]} this
						invitation.
					</p>
				) : (
					<div className={styles.inviteActions}>
						<Button
							size="sm"
							kind="primary"
							disabled={respond.isPending}
							onClick={() => respond.mutate(InviteResponse.Accept)}
						>
							Accept
						</Button>
						<Button
							size="sm"
							kind="tertiary"
							disabled={respond.isPending}
							onClick={() => respond.mutate(InviteResponse.Tentative)}
						>
							Tentative
						</Button>
						<Button
							size="sm"
							kind="danger--tertiary"
							disabled={respond.isPending}
							onClick={() => respond.mutate(InviteResponse.Decline)}
						>
							Decline
						</Button>
					</div>
				)
			) : (
				<p className={styles.waiting}>
					Preparing this invitation for a response…
				</p>
			)}
		</div>
	);
}
