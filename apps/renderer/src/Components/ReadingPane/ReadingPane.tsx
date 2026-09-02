import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { Button, SkeletonText } from "@carbon/react";
import { InviteResponse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { MessageHtml } from "@mylomail/renderer/Components/MessageHtml/MessageHtml";
import { AttachmentList } from "@mylomail/renderer/Components/AttachmentList/AttachmentList";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/ReadingPane/ReadingPane.module.css";

interface MessageInvite {
	eventId: string | null;
	title: string;
	start: string;
	end: string;
	organizer: { name: string | null; email: string } | null;
	myResponseStatus: InviteResponse | null;
}

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
}: {
	hub: HubConnection;
	messageId: string;
	subject: string;
	/**
	 * The message's first `From` address, for the remote-content allow list (§13 Epic 5).
	 * Empty in a popped-out message window, which has no message-list selection to carry it
	 * from — the pane still works there, just without the "always allow this sender" option.
	 */
	senderAddress?: string;
	/** Absent inside a window that is already just this one message (§13 Epic 10). */
	onOpenInNewWindow?: () => void;
}) {
	const body = useQuery({
		queryKey: ["body", messageId],
		queryFn: () => hub.invoke<MessageBody>("GetMessageBody", messageId),
		// Content lands after the message does, so an unfetched body is worth asking about
		// again; a fetched one never changes unless its raw content is replaced. A failed one
		// never will, having already exhausted its retries server-side (§15) — polling it
		// forever would just be asking the same unanswerable question every two seconds.
		refetchInterval: (query) =>
			query.state.data?.isFetched || query.state.data?.isFailed ? false : 2000,
	});

	return (
		<article className={styles.pane} aria-label="Message">
			<div className={styles.subjectRow}>
				<h2 className={styles.subject}>{subject || "(no subject)"}</h2>
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

	if (!invite.data) return null;

	const when = `${new Date(invite.data.start).toLocaleString()} – ${new Date(
		invite.data.end,
	).toLocaleTimeString()}`;

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
				invite.data.myResponseStatus !== null ? (
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
