import { useEffect, useRef, useState } from "react";
import {
	Button,
	DatePicker,
	DatePickerInput,
	OverflowMenu,
	OverflowMenuItem,
	TextInput,
	TimePicker,
} from "@carbon/react";
import { Editor } from "@mylomail/renderer/Components/Editor/Editor";
import type { HubConnection } from "@microsoft/signalr";
import type { ComposeSeed } from "@mylomail/renderer/Components/Compose/ComposeReplyForward";
import styles from "@mylomail/renderer/Components/Compose/Compose.module.css";

interface Sent {
	outboxItemId: string;
	cancelled: boolean;
	/** Absent for a normal send (the undo-send delay applies); set for a genuine schedule. */
	scheduledFor?: Date;
}

interface DraftAttachment {
	id: string;
	filename: string;
	mimeType: string;
	size: number;
}

export interface OpenDraft {
	id: string;
	inReplyToMessageId?: string | null;
	to: { name: string | null; email: string }[];
	cc: { name: string | null; email: string }[];
	bcc: { name: string | null; email: string }[];
	subject: string;
	bodyHtml: string;
	attachments: DraftAttachment[];
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
	onDetach,
	draft,
	seed,
}: {
	hub: HubConnection;
	accountId: string;
	onClose: () => void;
	/**
	 * Pops this draft into its own window (§13 Epic 10). Absent inside a window that is
	 * already just this draft — a compose window cannot detach from itself.
	 */
	onDetach?: (draftId: string) => void;
	draft?: OpenDraft;
	/**
	 * Prefill for a reply/reply-all/forward that has no saved draft yet (§13). Ignored once
	 * `draft` is given — an existing draft's own saved fields always win, since reopening one
	 * must show what was actually saved, not the seed it may once have started from.
	 */
	seed?: ComposeSeed;
}) {
	const [to, setTo] = useState(() => formatAddresses(draft?.to ?? seed?.to));
	const [cc, setCc] = useState(() => formatAddresses(draft?.cc ?? seed?.cc));
	const [bcc, setBcc] = useState(() =>
		formatAddresses(draft?.bcc ?? seed?.bcc),
	);
	const [subject, setSubject] = useState(draft?.subject ?? seed?.subject ?? "");
	const [body, setBody] = useState(draft?.bodyHtml ?? seed?.bodyHtml ?? "");
	const [sent, setSent] = useState<Sent | null>(null);
	const [busy, setBusy] = useState(false);
	const [draftId, setDraftId] = useState<string | null>(draft?.id ?? null);
	const [savedAt, setSavedAt] = useState<string | null>(null);
	const [attachments, setAttachments] = useState<DraftAttachment[]>(
		draft?.attachments ?? [],
	);
	const [forwardCopyError, setForwardCopyError] = useState<string | null>(null);
	// Send-later's own picker, distinct from "sent" state below: choosing a date/time does
	// not commit to anything until "Schedule" is pressed, same as a plain "Send" click does
	// not commit until it resolves.
	const [schedulePickerOpen, setSchedulePickerOpen] = useState(false);
	const [scheduleDate, setScheduleDate] = useState<Date | null>(null);
	const [scheduleTime, setScheduleTime] = useState("");
	const fileInput = useRef<HTMLInputElement>(null);

	// Threaded through to every save rather than fixed at construction: a reply/forward's
	// destination-message linkage must survive every subsequent autosave, not just the first
	// one, or re-editing an existing reply draft would silently un-thread it (§13).
	const inReplyToMessageId =
		draft?.inReplyToMessageId ?? seed?.inReplyToMessageId ?? null;

	// `save` always reads the latest field values through this ref rather than closing over
	// state, because a queued save (below) can run well after the render that scheduled it.
	const fieldsRef = useRef({
		to,
		cc,
		bcc,
		subject,
		body,
		draftId,
		inReplyToMessageId,
	});
	useEffect(() => {
		fieldsRef.current = {
			to,
			cc,
			bcc,
			subject,
			body,
			draftId,
			inReplyToMessageId,
		};
	});

	// Autosave, the manual "Save draft" button, "Send" and "Open in new window" all call
	// `save()`, and any two of them can overlap — most obviously the debounced autosave firing
	// while a manual save or send is already in flight. Two concurrent `SaveDraft` calls with
	// the same (still-null, for a brand new draft) `draftId` would each create their own draft
	// rather than one updating the other, orphaning one on the server; even once a draftId
	// exists, whichever response resolved last would win the `draftId`/`savedAt` state
	// regardless of which save was actually newer. Chaining every save onto the previous one's
	// promise serialises them, so each runs against the draft id the one before it produced.
	const saveChain = useRef<Promise<string>>(Promise.resolve(draft?.id ?? ""));
	const save = (): Promise<string> => {
		const run = async (): Promise<string> => {
			const fields = fieldsRef.current;
			const saved = await hub.invoke<{ id: string }>("SaveDraft", {
				draftId: fields.draftId,
				accountId,
				inReplyToMessageId: fields.inReplyToMessageId,
				to: parseAddresses(fields.to),
				cc: parseAddresses(fields.cc),
				bcc: parseAddresses(fields.bcc),
				subject: fields.subject,
				bodyHtml: fields.body,
			});

			setDraftId(saved.id);
			fieldsRef.current = { ...fieldsRef.current, draftId: saved.id };
			setSavedAt(new Date().toLocaleTimeString());
			return saved.id;
		};
		const next = saveChain.current.then(run, run);
		saveChain.current = next;
		return next;
	};

	// Autosaved on a debounce so a popped-out window (§13 Epic 10) survives being closed
	// without discarding — closing a window is not a moment this component gets to intercept,
	// so the draft has to already be safe on the server by the time that happens, not saved in
	// response to it. Skipped while empty: an untouched compose pane should not litter Drafts.
	useEffect(() => {
		if (!to && !subject && !body) return;
		const timer = setTimeout(() => void save(), 2000);
		return () => clearTimeout(timer);
		// eslint-disable-next-line react-hooks/exhaustive-deps -- `save` reads fieldsRef, not these values, at run time
	}, [to, cc, bcc, subject, body]);

	// A forward's own attachments have to be copied onto the new draft server-side — there is
	// no "attach this other message's attachment" concept, only "upload bytes" — so this reads
	// each one back from the original message and re-uploads it the same way a dropped file
	// would be. Runs once, guarded by the ref: `seed` is stable for this component's lifetime
	// (a new reply/forward always gets a fresh `key`, remounting rather than re-running this),
	// but effects still re-fire on unrelated re-renders without a guard.
	const forwardAttachmentsCopied = useRef(false);
	useEffect(() => {
		const toCopy = seed?.forwardAttachments;
		if (!toCopy || forwardAttachmentsCopied.current) return;
		forwardAttachmentsCopied.current = true;

		void (async () => {
			setBusy(true);
			try {
				const id = await save();
				const failed: string[] = [];
				// Each attachment copied independently: one failing (a since-deleted
				// attachment, a network blip) must not silently drop the rest of a
				// multi-attachment forward.
				for (const attachment of toCopy.attachments) {
					try {
						const response = await fetch(
							`/messages/${toCopy.sourceMessageId}/attachments/${attachment.id}`,
						);
						if (!response.ok) throw new Error("fetch failed");
						await uploadAttachment(
							id,
							await response.blob(),
							attachment.filename,
						);
					} catch {
						failed.push(attachment.filename);
					}
				}
				if (failed.length > 0) {
					setForwardCopyError(
						`Couldn't copy ${failed.length === 1 ? "this attachment" : "these attachments"} from the original message: ${failed.join(", ")}.`,
					);
				}
			} finally {
				setBusy(false);
			}
		})();
		// eslint-disable-next-line react-hooks/exhaustive-deps -- runs once per mount, guarded above
	}, []);

	const detach = async () => {
		if (!onDetach) return;
		const id = await save();
		onDetach(id);
	};

	/**
	 * `scheduledFor` absent (or null) is a normal send — the account's undo-send delay
	 * applies. A given time is a genuine future schedule; both go through the same
	 * `SendDraft` hub method and the same outbox mechanism server-side (§15).
	 */
	const send = async (scheduledFor?: Date) => {
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
			const id = await save();
			const outboxItemId = await hub.invoke<string>(
				"SendDraft",
				id,
				scheduledFor ? scheduledFor.toISOString() : null,
			);
			setSent({ outboxItemId, cancelled: false, scheduledFor });
		} finally {
			setBusy(false);
			setSchedulePickerOpen(false);
		}
	};

	const scheduleForPreset = (preset: "tomorrow" | "monday") => {
		const target = new Date();
		target.setHours(8, 0, 0, 0);
		if (preset === "tomorrow") {
			target.setDate(target.getDate() + 1);
		} else {
			// Always the *next* Monday, even if today is already Monday — a preset never
			// means "in a few hours," which "today" could otherwise resolve to.
			const daysUntilMonday = (1 - target.getDay() + 7) % 7 || 7;
			target.setDate(target.getDate() + daysUntilMonday);
		}
		void send(target);
	};

	const scheduleCustom = () => {
		if (!scheduleDate || !scheduleTime) return;
		const [hours, minutes] = scheduleTime.split(":").map(Number);
		if (Number.isNaN(hours) || Number.isNaN(minutes)) return;
		const target = new Date(scheduleDate);
		target.setHours(hours, minutes, 0, 0);
		void send(target);
	};

	/** Shared by manual/dropped file uploads and forward's copy-from-original-message path. */
	const uploadAttachment = async (
		id: string,
		content: Blob,
		filename: string,
	): Promise<void> => {
		const form = new FormData();
		form.append("file", content, filename);
		const response = await fetch(`/drafts/${id}/attachments`, {
			method: "POST",
			body: form,
		});
		if (!response.ok) throw new Error(`Could not attach ${filename}.`);
		const attachment = (await response.json()) as DraftAttachment;
		setAttachments((current) => [...current, attachment]);
	};

	const addFiles = async (files: FileList | File[]) => {
		setBusy(true);
		try {
			const id = await save();
			for (const file of Array.from(files)) {
				await uploadAttachment(id, file, file.name);
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
						: sent.scheduledFor
							? `Scheduled for ${sent.scheduledFor.toLocaleString()}.`
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
				id="compose-cc"
				labelText="Cc"
				value={cc}
				onChange={(event) => setCc(event.target.value)}
			/>
			<TextInput
				id="compose-bcc"
				labelText="Bcc"
				value={bcc}
				onChange={(event) => setBcc(event.target.value)}
			/>
			<TextInput
				id="compose-subject"
				labelText="Subject"
				value={subject}
				onChange={(event) => setSubject(event.target.value)}
			/>
			<Editor
				onChange={setBody}
				initialHtml={draft?.bodyHtml ?? seed?.bodyHtml}
			/>
			<input
				className={styles.fileInput}
				ref={fileInput}
				type="file"
				multiple
				onChange={(event) =>
					event.target.files && void addFiles(event.target.files)
				}
			/>
			{forwardCopyError ? (
				<p className={styles.forwardCopyError} role="alert">
					{forwardCopyError}
				</p>
			) : null}
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
				<div className={styles.sendSplit}>
					<Button size="sm" disabled={busy || !to} onClick={() => void send()}>
						Send
					</Button>
					<OverflowMenu
						aria-label="Send later"
						size="sm"
						disabled={busy || !to}
						flipped
					>
						<OverflowMenuItem
							itemText="Tomorrow morning"
							onClick={() => scheduleForPreset("tomorrow")}
						/>
						<OverflowMenuItem
							itemText="Monday morning"
							onClick={() => scheduleForPreset("monday")}
						/>
						<OverflowMenuItem
							itemText="Pick date & time…"
							hasDivider
							onClick={() => setSchedulePickerOpen(true)}
						/>
					</OverflowMenu>
				</div>
				{schedulePickerOpen ? (
					<div className={styles.schedulePicker}>
						<DatePicker
							datePickerType="single"
							minDate={new Date()}
							onChange={(dates) => setScheduleDate(dates[0] ?? null)}
						>
							<DatePickerInput
								id="compose-schedule-date"
								labelText="Date"
								placeholder="mm/dd/yyyy"
							/>
						</DatePicker>
						<TimePicker
							id="compose-schedule-time"
							labelText="Time"
							value={scheduleTime}
							onChange={(event) => setScheduleTime(event.target.value)}
						/>
						<Button
							size="sm"
							disabled={busy || !scheduleDate || !scheduleTime}
							onClick={scheduleCustom}
						>
							Schedule
						</Button>
						<Button
							size="sm"
							kind="ghost"
							onClick={() => setSchedulePickerOpen(false)}
						>
							Cancel
						</Button>
					</div>
				) : null}
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
				{onDetach ? (
					<Button size="sm" kind="ghost" onClick={() => void detach()}>
						Open in new window
					</Button>
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

function formatAddresses(addresses: OpenDraft["to"] | undefined): string {
	return addresses?.map((address) => address.email).join(", ") ?? "";
}
