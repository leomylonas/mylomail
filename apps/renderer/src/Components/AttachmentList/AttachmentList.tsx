import { useQuery } from "@tanstack/react-query";
import { Button } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
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
	hub: HubConnection;
	messageId: string;
}) {
	const attachments = useQuery({
		queryKey: ["attachments", messageId],
		queryFn: () => hub.invoke<Attachment[]>("GetAttachmentMetadata", messageId),
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
			<h3>Attachments</h3>
			{visible.map((attachment) => (
				<div className={styles.item} key={attachment.id}>
					<span>
						{attachment.filename} ({formatSize(attachment.size)})
					</span>
					<div>
						<Button
							size="sm"
							kind="ghost"
							onClick={() => download(messageId, attachment)}
						>
							Save
						</Button>
						<Button
							size="sm"
							kind="tertiary"
							onClick={() => void open(messageId, attachment)}
						>
							Open
						</Button>
					</div>
				</div>
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
