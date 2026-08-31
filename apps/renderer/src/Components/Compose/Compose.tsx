import { useState } from "react";
import { Button, TextInput } from "@carbon/react";
import { Editor } from "@mylomail/renderer/Components/Editor/Editor";
import type { HubConnection } from "@microsoft/signalr";
import styles from "@mylomail/renderer/Components/Compose/Compose.module.css";

interface Sent {
	outboxItemId: string;
	cancelled: boolean;
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
		<div className={styles.compose}>
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
			<div className={styles.actions}>
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
