import { useRef, useState } from "react";
import { Button, TextInput } from "@carbon/react";
import { Editor } from "@mylomail/renderer/Components/Editor/Editor";
import type { HubConnection } from "@microsoft/signalr";
import styles from "@mylomail/renderer/Components/Compose/Compose.module.css";

interface Sent {
	outboxItemId: string;
	cancelled: boolean;
}

interface DraftAttachment {
	id: string;
	filename: string;
	mimeType: string;
	size: number;
}

/**
 * Compose and send.
 *
 * The body is composed in Lexical and stored as HTML (§12). The editor's own state is never
 * persisted: a draft's body is HTML and a sent message is MIME, so storing editor JSON would
 * tie the mail format to an editor version.
 */
export function Compose({
	hub,
	accountId,
	onClose,
}: {
	hub: HubConnection;
	accountId: string;
	onClose: () => void;
}) {
	const [to, setTo] = useState("");
	const [subject, setSubject] = useState("");
	const [body, setBody] = useState("");
	const [sent, setSent] = useState<Sent | null>(null);
	const [busy, setBusy] = useState(false);
	const [draftId, setDraftId] = useState<string | null>(null);
	const [savedAt, setSavedAt] = useState<string | null>(null);
	const [attachments, setAttachments] = useState<DraftAttachment[]>([]);
	const fileInput = useRef<HTMLInputElement>(null);

	/**
	 * Saves without sending.
	 *
	 * The draft id is kept so a second save updates the same draft rather than creating
	 * another — on IMAP an update is an append plus an expunge of the old copy, and a new id
	 * each time would leave the server accumulating half-written messages.
	 */
	const save = async (): Promise<string> => {
		const draft = await hub.invoke<{ id: string }>("SaveDraft", {
			draftId,
			accountId,
			inReplyToMessageId: null,
			to: parseAddresses(to),
			cc: [],
			bcc: [],
			subject,
			bodyHtml: body,
		});

		setDraftId(draft.id);
		setSavedAt(new Date().toLocaleTimeString());
		return draft.id;
	};

	const send = async () => {
		if (
			!attachments.length &&
			/\b(attached|attachment|attach)\b/i.test(body) &&
			!window.confirm(
				"Your message mentions an attachment, but none is attached. Send anyway?",
			)
		) {
			return;
		}
		setBusy(true);
		try {
			const draft = await hub.invoke<{ id: string }>("SaveDraft", {
				draftId,
				accountId,
				inReplyToMessageId: null,
				to: parseAddresses(to),
				cc: [],
				bcc: [],
				subject,
				bodyHtml: body,
			});

			const outboxItemId = await hub.invoke<string>("SendDraft", draft.id);
			setSent({ outboxItemId, cancelled: false });
		} finally {
			setBusy(false);
		}
	};

	const addFiles = async (files: FileList | File[]) => {
		setBusy(true);
		try {
			const id = await save();
			for (const file of Array.from(files)) {
				const form = new FormData();
				form.append("file", file);
				const response = await fetch(`/drafts/${id}/attachments`, {
					method: "POST",
					body: form,
				});
				if (!response.ok) throw new Error(`Could not attach ${file.name}.`);
				const attachment = (await response.json()) as DraftAttachment;
				setAttachments((current) => [...current, attachment]);
			}
		} finally {
			setBusy(false);
		}
	};

	const removeAttachment = async (attachmentId: string) => {
		if (!draftId) return;
		setBusy(true);
		try {
			const response = await fetch(
				`/drafts/${draftId}/attachments/${attachmentId}`,
				{ method: "DELETE" },
			);
			if (!response.ok) throw new Error("Could not remove the attachment.");
			setAttachments((current) =>
				current.filter((attachment) => attachment.id !== attachmentId),
			);
		} finally {
			setBusy(false);
		}
	};

	// Undo is a compare-and-swap against the worker, not a check: it can lose, and when it
	// does the honest answer is that the message has gone (§15).
	const undo = async () => {
		if (!sent) return;
		const cancelled = await hub.invoke<boolean>(
			"CancelScheduledSend",
			sent.outboxItemId,
		);
		setSent({ ...sent, cancelled });
	};

	if (sent) {
		return (
			<div className={styles.compose}>
				<p className={styles.sent}>
					{sent.cancelled
						? "Sending cancelled. Your message was not sent."
						: "Sending…"}
				</p>
				<div className={styles.actions}>
					{sent.cancelled ? null : (
						<Button size="sm" kind="tertiary" onClick={() => void undo()}>
							Undo send
						</Button>
					)}
					<Button size="sm" kind="ghost" onClick={onClose}>
						Close
					</Button>
				</div>
			</div>
		);
	}

	return (
		<div
			className={styles.compose}
			onDragOver={(event) => event.preventDefault()}
			onDrop={(event) => {
				event.preventDefault();
				void addFiles(event.dataTransfer.files);
			}}
		>
			<TextInput
				id="compose-to"
				labelText="To"
				value={to}
				onChange={(event) => setTo(event.target.value)}
			/>
			<TextInput
				id="compose-subject"
				labelText="Subject"
				value={subject}
				onChange={(event) => setSubject(event.target.value)}
			/>
			<Editor onChange={setBody} />
			<input
				className={styles.fileInput}
				ref={fileInput}
				type="file"
				multiple
				onChange={(event) =>
					event.target.files && void addFiles(event.target.files)
				}
			/>
			{attachments.length ? (
				<ul className={styles.attachments} aria-label="Attached files">
					{attachments.map((attachment) => (
						<li key={attachment.id}>
							{attachment.filename}
							<Button
								size="sm"
								kind="ghost"
								disabled={busy}
								onClick={() => void removeAttachment(attachment.id)}
							>
								Remove
							</Button>
						</li>
					))}
				</ul>
			) : null}
			<div className={styles.actions}>
				<Button
					size="sm"
					kind="tertiary"
					disabled={busy}
					onClick={() => fileInput.current?.click()}
				>
					Attach files
				</Button>
				<Button size="sm" disabled={busy || !to} onClick={() => void send()}>
					Send
				</Button>
				<Button
					size="sm"
					kind="tertiary"
					disabled={busy}
					onClick={() => void save()}
				>
					Save draft
				</Button>
				{savedAt ? (
					<span className={styles.sent}>Saved at {savedAt}</span>
				) : null}
				<Button size="sm" kind="ghost" onClick={onClose}>
					Discard
				</Button>
			</div>
		</div>
	);
}

/** Splits a comma-separated recipient list, keeping only what looks like an address. */
function parseAddresses(
	input: string,
): { name: string | null; email: string }[] {
	return input
		.split(",")
		.map((part) => part.trim())
		.filter((part) => part.includes("@"))
		.map((email) => ({ name: null, email }));
}
