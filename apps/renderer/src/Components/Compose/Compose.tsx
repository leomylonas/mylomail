import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import {
	ActionableNotification,
	Button,
	ComboBox,
	DatePicker,
	DatePickerInput,
	OverflowMenu,
	OverflowMenuItem,
	Select,
	SelectItem,
	SkeletonText,
	TextInput,
	TimePicker,
} from "@carbon/react";
import { Editor } from "@mylomail/renderer/Components/Editor/Editor";
import type { HubConnection } from "@microsoft/signalr";
import type {
	AttachmentConstraintsDto,
	ContactSuggestionDto,
	OutboxItemDto,
	SendIdentityDto,
} from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { OutboxStatus } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { describeSentState } from "@mylomail/renderer/Components/Compose/SentStatus";
import {
	mentionsAttachmentOutsideQuote,
	type ComposeSeed,
} from "@mylomail/renderer/Components/Compose/ComposeReplyForward";
import { applyIdentitySignature } from "@mylomail/renderer/Components/Compose/ComposeSignature";
import {
	fetchApi,
	notificationForError,
} from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import styles from "@mylomail/renderer/Components/Compose/Compose.module.css";

interface Sent {
	outboxItemId: string;
	cancelled: boolean;
	/** Absent for a normal send (the undo-send delay applies); set for a genuine schedule. */
	scheduledFor?: Date;
	/**
	 * The item's live status, kept in sync via `OutboxStatusChanged` (§7, §15) so this window
	 * reflects what actually happened rather than sitting on "Sending…" forever once the
	 * worker takes the item — undefined only in the instant between `SendDraft` returning and
	 * the first announcement arriving.
	 */
	status?: OutboxStatus;
	lastError?: string;
	reconcilingSince?: Date;
	/** An undo attempt lost its compare-and-swap against the send worker; see `SentState`. */
	undoRejected?: boolean;
}

interface DraftAttachment {
	id: string;
	filename: string;
	mimeType: string;
	size: number;
	/** Embedded in the body via a `cid:` reference, not a manageable attachment (§13) — an
	 * inline image is never shown or removable in the "Attached files" list below, since
	 * removing it would leave that reference pointing at nothing with no way to fix the body. */
	isInline?: boolean;
}

export interface OpenDraft {
	id: string;
	sendIdentityId?: string | null;
	inReplyToMessageId?: string | null;
	to: { name: string | null; email: string }[];
	cc: { name: string | null; email: string }[];
	bcc: { name: string | null; email: string }[];
	subject: string;
	bodyHtml: string;
	attachments: DraftAttachment[];
	/** The server's copy changed while this was being edited locally (§1, §15) — both are
	 * kept until the user resolves it, never silently overwritten either direction. */
	syncConflict?: boolean;
}

interface RecipientSuggestion {
	email: string;
	label: string;
}

export function appendRecipient(value: string, email: string): string {
	const recipients = value
		.split(",")
		.map((recipient) => recipient.trim())
		.filter(Boolean);
	if (
		!recipients.some(
			(recipient) => recipient.toLowerCase() === email.toLowerCase(),
		)
	)
		recipients.push(email);
	return recipients.join(", ");
}

function RecipientField({
	id,
	label,
	value,
	suggestions,
	onChange,
}: {
	id: string;
	label: string;
	value: string;
	suggestions: RecipientSuggestion[];
	onChange: (value: string) => void;
}) {
	const [pickerVersion, setPickerVersion] = useState(0);
	return (
		<>
			<TextInput
				id={id}
				labelText={label}
				value={value}
				onChange={(event) => onChange(event.target.value)}
			/>
			<ComboBox
				key={pickerVersion}
				id={`${id}-contact`}
				titleText={`Add contact to ${label}`}
				placeholder="Search contacts"
				items={suggestions}
				itemToString={(item) => item?.label ?? ""}
				onChange={({ selectedItem }) => {
					if (!selectedItem) return;
					onChange(appendRecipient(value, selectedItem.email));
					setPickerVersion((version) => version + 1);
				}}
			/>
		</>
	);
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
	const { store: notifications } = useWindowNotifications();
	// save() itself keeps throwing rather than reporting internally: send/detach/the
	// forward-attachment-copy effect all await it and need to know it failed so they can skip
	// their own next step, not proceed as if a draft id existed. Every top-level, fire-and-
	// forget entry point below reports on its own catch instead.
	const reportFailure = (title: string) => (error: unknown) =>
		notify(notifications, notificationForError(error, title));

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
	const [syncConflict, setSyncConflict] = useState(
		draft?.syncConflict ?? false,
	);
	// Guards the DraftUpdated listener below against applying a GetDrafts response read from
	// before the user's own resolveConflict() call against one dispatched after it.
	const resolutionGeneration = useRef(0);
	// Checked before send (§15) — reported honestly rather than as a single number, since a
	// tenant's real Exchange message-size cap frequently isn't discoverable at all.
	const attachmentConstraints = useQuery({
		queryKey: ["attachmentConstraints", accountId],
		queryFn: () =>
			hub.invoke<AttachmentConstraintsDto>(
				"GetAttachmentConstraints",
				accountId,
			),
	});
	const contacts = useQuery({
		queryKey: ["contacts", accountId],
		queryFn: () =>
			hub.invoke<ContactSuggestionDto[]>("GetContactSuggestions", accountId),
	});
	const contactSuggestions = useMemo(
		() =>
			contacts.data?.flatMap((contact) =>
				contact.addresses.map((address) => ({
					email: address.email,
					label:
						contact.displayName &&
						contact.displayName.toLowerCase() !== address.email.toLowerCase()
							? `${contact.displayName} <${address.email}>`
							: address.email,
				})),
			) ?? [],
		[contacts.data],
	);
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

	// Null means "not chosen yet" — an existing draft's saved identity always wins once
	// identities load; a brand-new draft picks the account's default at that point (§15).
	const [identities, setIdentities] = useState<SendIdentityDto[]>([]);
	const [sendIdentityId, setSendIdentityId] = useState<string | null>(
		draft?.sendIdentityId ?? null,
	);
	// `Editor` (Lexical) only ever reads its `initialHtml` once, at construction — it never
	// re-syncs from a later `body` change. A brand-new draft's signature has to be appended
	// to `body` *before* `Editor` first mounts, or it would silently exist in state/on save
	// but never appear on screen. An existing draft needs no wait: its saved body already
	// reflects whatever signature the user kept, edited or removed.
	const [editorReady, setEditorReady] = useState(() => Boolean(draft));
	const [editorRevision, setEditorRevision] = useState(0);
	useEffect(() => {
		void (async () => {
			const list = await hub.invoke<SendIdentityDto[]>(
				"GetSendIdentities",
				accountId,
			);
			setIdentities(list);
			if (sendIdentityId) return;

			const chosen = list.find((identity) => identity.isDefault) ?? list[0];
			if (chosen) {
				setSendIdentityId(chosen.id);
				// Appended after any reply/forward quote a seed already placed, per §15's
				// "below quoted reply text" convention. The managed boundary survives
				// Lexical HTML round-trips so a later identity change can replace it exactly.
				if (!draft && chosen.signatureHtml) {
					setBody((current) => applyIdentitySignature(current, chosen));
				}
			}
		})()
			.catch(reportFailure("The send-from addresses could not be loaded"))
			.finally(() => setEditorReady(true));
		// eslint-disable-next-line react-hooks/exhaustive-deps -- runs once per mount
	}, []);

	// `save` always reads the latest field values through this ref rather than closing over
	// state, because a queued save (below) can run well after the render that scheduled it.
	const fieldsRef = useRef({
		to,
		cc,
		bcc,
		subject,
		body,
		draftId,
		sendIdentityId,
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
			sendIdentityId,
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
				sendIdentityId: fields.sendIdentityId,
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
		const timer = setTimeout(
			() => void save().catch(reportFailure("This draft could not be saved")),
			2000,
		);
		return () => clearTimeout(timer);
		// eslint-disable-next-line react-hooks/exhaustive-deps -- `save` reads fieldsRef, not these values, at run time
	}, [to, cc, bcc, subject, body]);

	/** Shared by manual/dropped file uploads and the reply/forward copy-from-original path. */
	const uploadAttachment = async (
		id: string,
		content: Blob,
		filename: string,
		isInline = false,
		contentId: string | null = null,
	): Promise<void> => {
		const form = new FormData();
		form.append("file", content, filename);
		if (isInline) {
			form.append("isInline", "true");
			if (contentId) {
				form.append("contentId", contentId);
			}
		}
		const response = await fetchApi(`/drafts/${id}/attachments`, {
			method: "POST",
			body: form,
		});
		const attachment = (await response.json()) as DraftAttachment;
		setAttachments((current) => [...current, attachment]);
	};

	// A reply's or forward's own attachments have to be copied onto the new draft server-side —
	// there is no "attach this other message's attachment" concept, only "upload bytes" — so
	// this reads each one back from the original message and re-uploads it the same way a
	// dropped file would be, preserving isInline/contentId so an inline image keeps the cid:
	// binding the quoted HTML already references. Runs once, guarded by the ref: `seed` is
	// stable for this component's lifetime (a new reply/forward always gets a fresh `key`,
	// remounting rather than re-running this), but effects still re-fire on unrelated re-renders
	// without a guard.
	const attachmentsCopied = useRef(false);
	useEffect(() => {
		const toCopy = seed?.attachmentsToCopy;
		if (!toCopy || attachmentsCopied.current) return;
		attachmentsCopied.current = true;

		void (async () => {
			setBusy(true);
			try {
				const id = await save();
				const failed: string[] = [];
				// Each attachment copied independently: one failing (a since-deleted
				// attachment, a network blip) must not silently drop the rest of a
				// multi-attachment reply/forward.
				for (const attachment of toCopy.attachments) {
					try {
						const response = await fetchApi(
							`/messages/${toCopy.sourceMessageId}/attachments/${attachment.id}`,
						);
						await uploadAttachment(
							id,
							await response.blob(),
							attachment.filename,
							attachment.isInline,
							attachment.contentId,
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
			} catch (error) {
				reportFailure("This draft could not be saved")(error);
			} finally {
				setBusy(false);
			}
		})();
		// eslint-disable-next-line react-hooks/exhaustive-deps -- runs once per mount, guarded above
	}, []);

	// "Keep mine" (true) abandons the conflicting remote draft and pushes this content fresh;
	// "keep theirs" (false) discards local edits and adopts the server's actual current
	// content — both go through the same ResolveDraftConflict hub method (§1, §15).
	const resolveConflict = async (keepMine: boolean) => {
		if (!draftId) return;
		try {
			// Bumped before the resolve's own await, not after: a DraftUpdated refetch already
			// in flight when the user clicks resolve must not re-flip the banner back on with
			// data read before this resolution happened (see the generation guard below).
			resolutionGeneration.current += 1;
			const resolved = await hub.invoke<OpenDraft>(
				"ResolveDraftConflict",
				draftId,
				keepMine,
			);
			setSyncConflict(resolved.syncConflict ?? false);
			if (!keepMine) {
				setTo(formatAddresses(resolved.to));
				setCc(formatAddresses(resolved.cc));
				setBcc(formatAddresses(resolved.bcc));
				setSubject(resolved.subject);
				setBody(resolved.bodyHtml);
				setAttachments(resolved.attachments);
			}
		} catch (error) {
			reportFailure("This conflict could not be resolved")(error);
		}
	};

	const detach = async () => {
		if (!onDetach) return;
		try {
			const id = await save();
			onDetach(id);
		} catch (error) {
			reportFailure("This draft could not be saved")(error);
		}
	};

	/**
	 * `scheduledFor` absent (or null) is a normal send — the account's undo-send delay
	 * applies. A given time is a genuine future schedule; both go through the same
	 * `SendDraft` hub method and the same outbox mechanism server-side (§15).
	 */
	const send = async (scheduledFor?: Date) => {
		const constraints = attachmentConstraints.data;
		if (attachments.length && constraints) {
			// Base64 encoding inflates raw bytes by 4/3 — the check is against what actually
			// goes out on the wire, not the file sizes on disk (§15).
			const encodedTotal = attachments.reduce(
				(sum, a) => sum + Math.ceil(a.size * (4 / 3)),
				0,
			);
			const totalLimit =
				constraints.configuredOverride ?? constraints.knownMessageSizeLimit;
			const oversizedFile = constraints.apiPerFileLimit
				? attachments.find((a) => a.size > constraints.apiPerFileLimit!)
				: undefined;
			if (oversizedFile) {
				window.alert(
					`"${oversizedFile.filename}" is larger than this account's per-file attachment limit. Remove or shrink it before sending.`,
				);
				return;
			}
			if (totalLimit && encodedTotal > totalLimit) {
				window.alert(
					"These attachments are too large for this account to send. Remove some before sending.",
				);
				return;
			}
			if (
				constraints.isUnknown &&
				!window.confirm(
					"This account's attachment size limit couldn't be determined. Send anyway?",
				)
			) {
				return;
			}
		}
		if (
			!attachments.length &&
			mentionsAttachmentOutsideQuote(body) &&
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
		} catch (error) {
			reportFailure(
				scheduledFor
					? "This message could not be scheduled"
					: "This message could not be sent",
			)(error);
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
		// DatePicker's minDate only excludes past *days* — picking today plus an earlier time
		// than now still reaches here. OutboxService.QueueAsync/OutboxDispatcher.RequestSend
		// both clamp a past ScheduledSendAt to an immediate send rather than rejecting it, so
		// silently proceeding here would send right now while the user still believes they
		// picked a future moment and can walk away — the opposite of what clicking "Schedule"
		// (as distinct from the ordinary Send button) communicates.
		if (target.getTime() <= Date.now()) {
			window.alert(
				"That time has already passed. Pick a time later than now, or use Send instead.",
			);
			return;
		}
		void send(target);
	};

	// Each file is attempted independently, mirroring the forward-attachment-copy effect below:
	// one oversized or rejected file must not silently drop every file dropped alongside it.
	const addFiles = async (files: FileList | File[]) => {
		setBusy(true);
		try {
			const id = await save();
			const failed: string[] = [];
			for (const file of Array.from(files)) {
				try {
					await uploadAttachment(id, file, file.name);
				} catch {
					failed.push(file.name);
				}
			}
			if (failed.length > 0) {
				notify(notifications, {
					kind: "error",
					title: "Some attachments could not be added",
					detail: failed.join(", "),
				});
			}
		} catch (error) {
			reportFailure("This attachment could not be added")(error);
		} finally {
			setBusy(false);
		}
	};

	const removeAttachment = async (attachmentId: string) => {
		if (!draftId) return;
		setBusy(true);
		try {
			await fetchApi(`/drafts/${draftId}/attachments/${attachmentId}`, {
				method: "DELETE",
			});
			setAttachments((current) =>
				current.filter((attachment) => attachment.id !== attachmentId),
			);
		} catch (error) {
			reportFailure("This attachment could not be removed")(error);
		} finally {
			setBusy(false);
		}
	};

	// Undo is a compare-and-swap against the worker, not a check: it can lose, and when it
	// does the honest answer is that the message has gone (§15).
	const undo = async () => {
		if (!sent) return;
		try {
			const cancelled = await hub.invoke<boolean>(
				"CancelScheduledSend",
				sent.outboxItemId,
			);
			// Functional update, not a spread of the closure-captured `sent`: a genuine
			// OutboxStatusChanged can land while this call is still in flight, and overwriting
			// it with a stale copy here would revert a real "Sent"/"Failed" back to whatever
			// this closure saw when undo() was first called.
			setSent((current) =>
				current ? { ...current, cancelled, undoRejected: !cancelled } : current,
			);
		} catch (error) {
			reportFailure("The send could not be undone")(error);
		}
	};

	// Without this, this window never learns what actually happened after the undo-send
	// window closes: OutboxService announces every status transition (§7, §15) specifically
	// so a watcher isn't left staring at "Sending…" once the worker takes the item, but until
	// now nothing in the renderer subscribed to it.
	useEffect(() => {
		const onStatusChanged = (item: OutboxItemDto) => {
			setSent((current) =>
				current && item.id === current.outboxItemId
					? {
							...current,
							status: item.status,
							lastError: item.lastError ?? undefined,
							reconcilingSince: item.reconcilingSince
								? new Date(item.reconcilingSince)
								: undefined,
						}
					: current,
			);
		};
		hub.on("OutboxStatusChanged", onStatusChanged);
		return () => hub.off("OutboxStatusChanged", onStatusChanged);
	}, [hub]);

	// A draft already open here can be flagged SyncConflict by a background sync running
	// concurrently (a remote materialisation, or another client's own push) — DraftSyncService
	// then correctly refuses to push this window's edits until it's resolved (§1, §15), but
	// without this listener the open window never learns that happened: it would keep
	// accepting Send/local-save with no banner and no indication anything stopped syncing,
	// discoverable only by closing and reopening the draft. DraftUpdated carries no draftId
	// (§7 — broadcast is by prefix, not scoped), so this re-fetches the account's draft list
	// and checks whether the one open here is affected, the same shape DraftList.tsx's own
	// listener already uses. Deliberately one-directional: it only ever flips syncConflict
	// false -> true, never touches to/cc/bcc/subject/body, so it can't clobber the very edits
	// the conflict banner exists to protect — going back false happens only through the
	// existing explicit resolveConflict() call.
	useEffect(() => {
		if (!draftId) return;
		const onDraftUpdated = () => {
			// A resolve started after this fetch was dispatched can still be in flight when
			// GetDrafts' response — read before that resolution — comes back, since nothing
			// orders a broadcast-triggered refetch against a concurrent resolveConflict() call.
			// Snapshotting the generation here and rejecting a stale response below stops that
			// race from re-flipping the banner back on immediately after the user resolved it.
			const generation = resolutionGeneration.current;
			void hub
				.invoke<OpenDraft[]>("GetDrafts", accountId)
				.then((drafts) => {
					if (resolutionGeneration.current !== generation) return;
					const match = drafts.find((item) => item.id === draftId);
					if (match?.syncConflict) {
						setSyncConflict(true);
					}
				})
				.catch(() => {
					// Best-effort: a failed refresh here leaves the window exactly as
					// informed as it already was, not worse off.
				});
		};
		hub.on("DraftUpdated", onDraftUpdated);
		return () => hub.off("DraftUpdated", onDraftUpdated);
	}, [hub, accountId, draftId]);

	// Standard Gmail/Outlook convention (§13): Ctrl+Enter, or Cmd+Enter on macOS, sends
	// from anywhere in the compose window, including inside the editor itself. A window
	// listener rather than a JSX onKeyDown, since the latter needs an interactive role/
	// tabIndex on this div for no real benefit — modified combinations aren't suppressed
	// by typing-target rules the way bare-letter shortcuts are (Shortcuts.ts), so a global
	// listener scoped to this component's own lifetime is exactly as safe. Mirrors the
	// Send button's own disabled condition exactly so this can't send something the
	// button itself would refuse to, and does nothing once a send has already gone out.
	useEffect(() => {
		if (sent) return;
		const onKeyDown = (event: KeyboardEvent) => {
			if (
				event.key === "Enter" &&
				(event.ctrlKey || event.metaKey) &&
				!(busy || !to || syncConflict)
			) {
				event.preventDefault();
				void send();
			}
		};
		window.addEventListener("keydown", onKeyDown);
		return () => window.removeEventListener("keydown", onKeyDown);
	}, [sent, busy, to, syncConflict, send]);

	if (sent) {
		const display = describeSentState(sent);
		return (
			<div className={styles.compose}>
				<p
					className={display.failed ? styles.sendFailed : styles.sent}
					role={display.failed ? "alert" : undefined}
				>
					{display.message}
				</p>
				<div className={styles.actions}>
					{display.canUndo ? (
						<Button size="sm" kind="tertiary" onClick={() => void undo()}>
							Undo send
						</Button>
					) : null}
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
			{syncConflict ? (
				<div className={styles.conflict}>
					<p>
						The server&apos;s copy of this draft changed since it was last read.
						Keep your changes and overwrite it, or discard them and take the
						server&apos;s version instead.
					</p>
					<div className={styles.conflictActions}>
						<Button
							size="sm"
							kind="tertiary"
							onClick={() => void resolveConflict(true)}
						>
							Keep mine
						</Button>
						<Button
							size="sm"
							kind="tertiary"
							onClick={() => void resolveConflict(false)}
						>
							Keep theirs
						</Button>
					</div>
				</div>
			) : null}
			{identities.length > 1 ? (
				<Select
					id="compose-from"
					labelText="From"
					value={sendIdentityId ?? ""}
					onChange={(event) => {
						const id = event.target.value;
						const identity = identities.find(
							(candidate) => candidate.id === id,
						);
						if (!identity) return;

						const nextBody = applyIdentitySignature(body, identity);
						setSendIdentityId(id);
						setBody(nextBody);
						setEditorRevision((current) => current + 1);
						// `fieldsRef` is otherwise only kept current by a passive effect that
						// runs after paint — too late for `save()`'s already-chained promise,
						// which can run as a microtask before that effect flushes. Update the
						// identity and its matching body as one save snapshot.
						fieldsRef.current = {
							...fieldsRef.current,
							body: nextBody,
							sendIdentityId: id,
						};
						void save().catch(reportFailure("This draft could not be saved"));
					}}
				>
					{identities.map((identity) => (
						<SelectItem
							key={identity.id}
							value={identity.id}
							text={`${identity.displayName} <${identity.emailAddress}>`}
						/>
					))}
				</Select>
			) : null}
			{contacts.isError ? (
				<ActionableNotification
					kind="error"
					title="Couldn't load contact suggestions"
					subtitle={
						contacts.error instanceof Error
							? contacts.error.message
							: String(contacts.error)
					}
					actionButtonLabel="Retry"
					onActionButtonClick={() => void contacts.refetch()}
					lowContrast
				/>
			) : null}
			<RecipientField
				id="compose-to"
				label="To"
				value={to}
				suggestions={contactSuggestions}
				onChange={setTo}
			/>
			<RecipientField
				id="compose-cc"
				label="Cc"
				value={cc}
				suggestions={contactSuggestions}
				onChange={setCc}
			/>
			<RecipientField
				id="compose-bcc"
				label="Bcc"
				value={bcc}
				suggestions={contactSuggestions}
				onChange={setBcc}
			/>
			<TextInput
				id="compose-subject"
				labelText="Subject"
				value={subject}
				onChange={(event) => setSubject(event.target.value)}
			/>
			{editorReady ? (
				<Editor key={editorRevision} onChange={setBody} initialHtml={body} />
			) : (
				<SkeletonText paragraph lineCount={4} />
			)}
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
			{attachments.some((attachment) => !attachment.isInline) ? (
				<ul className={styles.attachments} aria-label="Attached files">
					{attachments
						.filter((attachment) => !attachment.isInline)
						.map((attachment) => (
							<li key={attachment.id}>
								<span
									className={styles.attachmentName}
									title={attachment.filename}
								>
									{attachment.filename}
								</span>
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
					<Button
						size="sm"
						disabled={busy || !to || syncConflict}
						onClick={() => void send()}
					>
						Send
					</Button>
					<OverflowMenu
						aria-label="Send later"
						size="sm"
						disabled={busy || !to || syncConflict}
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
							disabled={busy || !scheduleDate || !scheduleTime || syncConflict}
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
					onClick={() =>
						void save().catch(reportFailure("This draft could not be saved"))
					}
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
