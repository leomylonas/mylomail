import { useQuery } from "@tanstack/react-query";
import { addressLabel } from "@mylomail/renderer/Lib/AddressDisplay";
import { Modal } from "@carbon/react";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { present } from "@mylomail/renderer/Shell/Registries/Errors/ErrorPresentation";
import { useDateFormats } from "@mylomail/renderer/Shell/DateFormats/DateFormats";
import type { MutationFailureDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { MutationOperationKind } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import styles from "@mylomail/renderer/Components/FailureDetailsDialog/FailureDetailsDialog.module.css";

/** What the user asked for, in their terms. */
export function describeOperation(kind: MutationOperationKind): string {
	switch (kind) {
		case MutationOperationKind.SetFlags:
			return "Mark as read, unread or flagged";
		case MutationOperationKind.MoveMessage:
			return "Move to another folder";
		case MutationOperationKind.RemoveFromMailbox:
			return "Remove from a folder or label";
		case MutationOperationKind.MoveToTrash:
			return "Move to Trash";
		case MutationOperationKind.DeletePermanently:
			return "Delete permanently";
		case MutationOperationKind.Send:
			return "Send";
		default:
			return "Change a message";
	}
}

/**
 * What a failed change was and why it did not happen: the operation, the message, the account and
 * the full reason, which the notification only has room to summarise.
 */
export function FailureDetailsDialog({
	hub,
	failure,
	accountName,
	onClose,
}: {
	hub: MailHubConnection;
	failure: MutationFailureDto;
	accountName: string | null;
	onClose: () => void;
}) {
	const dateFormats = useDateFormats();
	const presentation = present(failure.category, failure.detail);
	const message = useQuery({
		queryKey: ["message-reply-context", failure.messageId],
		queryFn: () => hub.getMessageReplyContext(failure.messageId),
		retry: false,
	});
	const from = message.data?.from?.[0];

	return (
		<Modal
			open
			passiveModal
			modalHeading="What went wrong"
			modalLabel={presentation.title}
			onRequestClose={onClose}
		>
			<dl className={styles.details}>
				<dt>What you asked for</dt>
				<dd>{describeOperation(failure.operationKind)}</dd>
				<dt>Message</dt>
				<dd>
					{message.data ? (
						<>
							{message.data.subject || "(no subject)"}
							{from ? ` — from ${addressLabel(from)}` : ""}
							{message.data.receivedAt
								? `, ${dateFormats.dateTime(new Date(message.data.receivedAt))}`
								: ""}
						</>
					) : message.isPending ? (
						"Loading…"
					) : (
						"This message is no longer in MyloMail."
					)}
				</dd>
				{accountName ? (
					<>
						<dt>Account</dt>
						<dd>{accountName}</dd>
					</>
				) : null}
				<dt>Why</dt>
				<dd>{presentation.detail}</dd>
			</dl>
		</Modal>
	);
}
