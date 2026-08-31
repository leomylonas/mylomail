import { useState } from "react";
import { Button, TextArea, TextInput } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import styles from "@mylomail/renderer/Components/Compose/Compose.module.css";

interface Sent {
	outboxItemId: string;
	cancelled: boolean;
}

/**
 * Compose and send.
 *
 * The body is plain text for now, escaped into HTML at save. §12 specifies Lexical for rich
 * composition, and this is deliberately not a hand-rolled substitute for it: a bespoke
 * editor would have to be unbuilt, whereas a textarea is obviously temporary and exercises
 * the whole send path underneath — which is the part with the crash-safety machinery.
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

	const send = async () => {
		setBusy(true);
		try {
			const draft = await hub.invoke<{ id: string }>("SaveDraft", {
				draftId: null,
				accountId,
				inReplyToMessageId: null,
				to: parseAddresses(to),
				cc: [],
				bcc: [],
				subject,
				bodyHtml: toHtml(body),
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
			<TextArea
				id="compose-body"
				labelText="Message"
				rows={10}
				value={body}
				onChange={(event) => setBody(event.target.value)}
			/>
			<div className={styles.actions}>
				<Button size="sm" disabled={busy || !to} onClick={() => void send()}>
					Send
				</Button>
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

/**
 * Escapes the typed text into HTML.
 *
 * Escaped rather than passed through: whatever the user types is text, and treating it as
 * markup would let a pasted fragment become live HTML in someone else's client.
 */
function toHtml(text: string): string {
	const escaped = text
		.replaceAll("&", "&amp;")
		.replaceAll("<", "&lt;")
		.replaceAll(">", "&gt;");

	return `<p>${escaped.replaceAll("\n", "<br>")}</p>`;
}
