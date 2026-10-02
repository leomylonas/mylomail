import { useQuery } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { Attachment, Download } from "@carbon/icons-react";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/AttachmentList/AttachmentList.module.css";

interface Attachment {
	id: string;
	filename: string;
	mimeType: string;
	size: number;
	isInline: boolean;
}

/** Received attachments: bytes stay in raw MIME until the user asks to download or open. */
export function AttachmentList({
	hub,
	messageId,
}: {
	hub: MailHubConnection;
	messageId: string;
}) {
	const { store: notifications } = useWindowNotifications();
	const attachments = useQuery({
		queryKey: ["attachments", messageId],
		queryFn: () => hub.getAttachmentMetadata(messageId),
	});

	if (attachments.isError)
		return (
			<p className={styles.error} role="alert">
				Could not load attachments.
			</p>
		);

	const visible =
		attachments.data?.filter((attachment) => !attachment.isInline) ?? [];
	if (!visible.length) return null;

	return (
		<section className={styles.list} aria-label="Attachments">
			{visible.map((attachment) => (
				<span className={styles.chip} key={attachment.id}>
					<button
						type="button"
						className={styles.open}
						title={`${attachment.filename} (${formatSize(attachment.size)})`}
						aria-label={`Open ${attachment.filename}`}
						onClick={() =>
							void open(messageId, attachment).catch((error: unknown) =>
								notify(
									notifications,
									notificationForError(
										error,
										"This attachment could not be opened",
									),
								),
							)
						}
					>
						<Attachment size={16} aria-hidden="true" />
						<span className={styles.filename}>{attachment.filename}</span>
						<span className={styles.size}>{formatSize(attachment.size)}</span>
					</button>
					<button
						type="button"
						className={styles.save}
						title="Save"
						aria-label={`Save ${attachment.filename}`}
						onClick={() => download(messageId, attachment)}
					>
						<Download size={16} aria-hidden="true" />
					</button>
				</span>
			))}
		</section>
	);
}

function download(messageId: string, attachment: Attachment) {
	const link = document.createElement("a");
	link.href = `/messages/${messageId}/attachments/${attachment.id}`;
	link.download = attachment.filename;
	link.click();
}

async function open(messageId: string, attachment: Attachment) {
	if (!window.backend)
		throw new Error("Attachment opening is available only in the desktop app.");
	const error = await window.backend.openAttachment(messageId, attachment.id);
	if (error) throw new Error(error);
}

function formatSize(size: number) {
	return size < 1024 * 1024
		? `${Math.max(1, Math.ceil(size / 1024))} KB`
		: `${(size / 1024 / 1024).toFixed(1)} MB`;
}
