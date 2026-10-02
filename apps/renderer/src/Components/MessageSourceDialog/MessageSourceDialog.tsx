import { useEffect, useRef } from "react";
import { Close } from "@carbon/icons-react";
import { Button, IconButton, SkeletonText } from "@carbon/react";
import { useQuery } from "@tanstack/react-query";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import styles from "@mylomail/renderer/Components/MessageSourceDialog/MessageSourceDialog.module.css";

/** Shown characters. A message with large attachments is megabytes of base64; the file is whole. */
const displayLimit = 400_000;

/**
 * The message exactly as stored: every header and every MIME part, unparsed. The raw bytes are
 * the single stored representation (§1), so this is the authoritative view of what arrived,
 * for diagnosing rendering, threading or authentication questions.
 */
export function MessageSourceDialog({
	hub,
	messageId,
	subject,
	onClose,
}: {
	hub: MailHubConnection;
	messageId: string;
	subject: string;
	onClose: () => void;
}) {
	const dialogRef = useRef<HTMLDialogElement>(null);
	const { store: notifications } = useWindowNotifications();

	// No cleanup close, for the reason given in SettingsDialog.
	useEffect(() => {
		const dialog = dialogRef.current;
		if (dialog && !dialog.open) dialog.showModal();
	}, []);

	const source = useQuery({
		queryKey: ["message-source", messageId],
		queryFn: async () => {
			const base64 = await hub.saveMessageAsEml(messageId);
			const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
			return { bytes, text: new TextDecoder("utf-8").decode(bytes) };
		},
		staleTime: Infinity,
		gcTime: 0,
	});

	const truncated = (source.data?.text.length ?? 0) > displayLimit;
	const report = (title: string) => (error: unknown) =>
		notify(notifications, notificationForError(error, title));

	return (
		<dialog
			ref={dialogRef}
			className={styles.dialog}
			aria-labelledby="message-source-title"
			onClose={onClose}
		>
			<header className={styles.header}>
				<h2 id="message-source-title">Message source</h2>
				<div className={styles.actions}>
					<Button
						size="sm"
						kind="ghost"
						disabled={!source.data}
						onClick={() =>
							void navigator.clipboard
								.writeText(source.data!.text)
								.catch(report("The source could not be copied"))
						}
					>
						Copy
					</Button>
					<Button
						size="sm"
						kind="ghost"
						disabled={!source.data}
						onClick={() => {
							const blob = new Blob([source.data!.bytes], {
								type: "message/rfc822",
							});
							const url = URL.createObjectURL(blob);
							try {
								const link = document.createElement("a");
								link.href = url;
								link.download = `${subject || "message"}.eml`;
								link.click();
							} finally {
								URL.revokeObjectURL(url);
							}
						}}
					>
						Save as .eml
					</Button>
					<IconButton
						label="Close message source"
						kind="ghost"
						size="md"
						align="bottom-end"
						onClick={onClose}
					>
						<Close size={20} />
					</IconButton>
				</div>
			</header>
			<div className={styles.body}>
				{source.isPending ? <SkeletonText paragraph lineCount={8} /> : null}
				{source.isError ? (
					<p role="alert">The message source could not be loaded.</p>
				) : null}
				{source.data ? (
					<>
						{truncated ? (
							<p className={styles.note}>
								Showing the first {displayLimit.toLocaleString()} characters.
								Save as .eml for the whole message.
							</p>
						) : null}
						<pre className={styles.source}>
							{truncated
								? source.data.text.slice(0, displayLimit)
								: source.data.text}
						</pre>
					</>
				) : null}
			</div>
		</dialog>
	);
}
