import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Modal, Tag, TextArea, TextInput, Toggle } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import { InviteResponse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import {
	fromInclusiveEndDateInputValue,
	toInclusiveEndDateInputValue,
} from "@mylomail/renderer/Components/Calendar/EventModal/AllDayEventEnd";
import styles from "@mylomail/renderer/Components/Calendar/EventModal/EventModal.module.css";

interface Attendee {
	name: string | null;
	email: string;
	role: number;
	responseStatus: number;
}

interface EventDetail {
	id: string;
	organizer: { name: string | null; email: string } | null;
	attendees: Attendee[];
	isOrganizer: boolean;
	myResponseStatus: InviteResponse | null;
	/** Absolute trigger times parsed from the source's own VALARM blocks (§1). Read-only. */
	reminders: string[];
	/** The row's own persisted start/end — the series' original start for a recurring master,
	 * not any particular occurrence's date (§13 Epic 7). */
	start: string;
	end: string;
	isAllDay: boolean;
}

const responseStatusLabel = [
	"Awaiting response",
	"Accepted",
	"Declined",
	"Tentative",
];

export interface EventFormValues {
	eventId?: string;
	calendarId: string;
	title: string;
	location: string;
	description: string;
	start: string;
	end: string;
	isAllDay: boolean;
}

/**
 * Create or edit one event.
 *
 * Local time only: the model carries a start/end timezone id for a reason (§1), but nothing
 * upstream of this form yet lets a user pick a zone other than the one their machine is in —
 * that is calendar-invite handling's problem, not calendar CRUD's (§13 Epic 7).
 */
export function EventModal({
	hub,
	initial,
	syncConflict,
	deletesWholeSeries,
	virtualOccurrence,
	onSave,
	onDelete,
	onResolveConflict,
	onClose,
}: {
	hub: HubConnection;
	initial: EventFormValues;
	syncConflict?: boolean;
	/**
	 * True when this event is a recurring series (a master, or a not-yet-materialised virtual
	 * occurrence routed to its master — §13 Epic 7's deferred per-occurrence editing). Deleting
	 * either one deletes the whole series, not just the occurrence the user opened, so the
	 * confirmation must say so rather than reading like an ordinary single-event delete.
	 */
	deletesWholeSeries?: boolean;
	/**
	 * True when `initial` was routed here from a not-yet-materialised virtual occurrence rather
	 * than a real row (§13 Epic 7) — `initial.start`/`end` are that occurrence's own derived
	 * date, not the master's. Saving unedited would silently reschedule the whole series to that
	 * date, since `SaveCalendarEvent` overwrites the master's Start/End with whatever the form
	 * holds. Once `GetCalendarEventDetail` resolves the master's real Start/End, this replaces
	 * the occurrence's date in the form so the fields shown match what a save would actually do.
	 */
	virtualOccurrence?: boolean;
	onSave: (values: EventFormValues) => void;
	onDelete?: () => void;
	/**
	 * "Keep mine" (`true`) force-overwrites the server; "keep theirs" (`false`) discards the
	 * local edit shown here and pulls the server's current version instead (§15). Absent for
	 * a new, unsaved event — there is nothing to conflict with yet.
	 */
	onResolveConflict?: (keepMine: boolean) => void;
	onClose: () => void;
}) {
	const [values, setValues] = useState(initial);
	const [comment, setComment] = useState("");
	const isNew = !initial.eventId;
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	const detail = useQuery({
		queryKey: ["calendar-event-detail", initial.eventId],
		queryFn: () =>
			hub.invoke<EventDetail>("GetCalendarEventDetail", initial.eventId),
		enabled: !isNew,
	});

	// Applied once, not on every `detail` refetch: after the user has started editing the
	// date/time themselves, a background refetch replacing their edit with the (unchanged)
	// master date would be as surprising as the bug this exists to prevent.
	const appliedMasterDate = useRef(false);
	useEffect(() => {
		if (virtualOccurrence && detail.data && !appliedMasterDate.current) {
			appliedMasterDate.current = true;
			setValues((current) => ({
				...current,
				start: detail.data.start,
				end: detail.data.end,
				isAllDay: detail.data.isAllDay,
			}));
		}
	}, [virtualOccurrence, detail.data]);

	const respond = useMutation({
		mutationFn: (response: InviteResponse) =>
			hub.invoke("RespondToInvite", initial.eventId, response, comment || null),
		onSuccess: () => {
			setComment("");
			void queryClient.invalidateQueries({
				queryKey: ["calendar-event-detail", initial.eventId],
			});
		},
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The response could not be sent",
				detail: error instanceof Error ? error.message : String(error),
			}),
	});

	return (
		<Modal
			open
			modalHeading={isNew ? "New event" : "Edit event"}
			primaryButtonText="Save"
			secondaryButtonText="Cancel"
			onRequestClose={onClose}
			onRequestSubmit={() => onSave(values)}
			danger={false}
		>
			<div className={styles.form}>
				{syncConflict ? (
					<div className={styles.conflict}>
						<p>
							The server&apos;s copy changed since this was last read. Keep your
							changes and overwrite it, or discard them and take the
							server&apos;s version instead.
						</p>
						{onResolveConflict ? (
							<div className={styles.conflictActions}>
								<Button
									size="sm"
									kind="tertiary"
									onClick={() => onResolveConflict(true)}
								>
									Keep mine
								</Button>
								<Button
									size="sm"
									kind="tertiary"
									onClick={() => onResolveConflict(false)}
								>
									Keep theirs
								</Button>
							</div>
						) : null}
					</div>
				) : null}
				<TextInput
					id="event-title"
					labelText="Title"
					value={values.title}
					onChange={(event) =>
						setValues({ ...values, title: event.target.value })
					}
				/>
				<TextInput
					id="event-location"
					labelText="Location"
					value={values.location}
					onChange={(event) =>
						setValues({ ...values, location: event.target.value })
					}
				/>
				<Toggle
					id="event-all-day"
					labelText="All day"
					toggled={values.isAllDay}
					onToggle={(checked) =>
						setValues({
							...values,
							isAllDay: checked,
							// Switching on: snap End to the RFC 5545 exclusive convention
							// (Start's day plus one) rather than carry over a same-day
							// timestamp that would render as a zero-duration span. Switching
							// off: the reverse would be equally wrong to leave in place, so
							// give the timed fields a real one-hour span to start from.
							end: checked
								? dayjs(values.start).startOf("day").add(1, "day").toISOString()
								: dayjs(values.start).add(1, "hour").toISOString(),
						})
					}
				/>
				<div className={styles.row}>
					<TextInput
						id="event-start"
						labelText="Start"
						type={values.isAllDay ? "date" : "datetime-local"}
						value={toInputValue(values.start, values.isAllDay)}
						onChange={(event) =>
							setValues({
								...values,
								start: fromInputValue(event.target.value, values.isAllDay),
							})
						}
					/>
					<TextInput
						id="event-end"
						labelText="End"
						type={values.isAllDay ? "date" : "datetime-local"}
						value={
							values.isAllDay
								? toInclusiveEndDateInputValue(values.end)
								: toInputValue(values.end, false)
						}
						onChange={(event) =>
							setValues({
								...values,
								end: values.isAllDay
									? fromInclusiveEndDateInputValue(event.target.value)
									: fromInputValue(event.target.value, false),
							})
						}
					/>
				</div>
				<TextArea
					id="event-description"
					labelText="Description"
					value={values.description}
					onChange={(event) =>
						setValues({ ...values, description: event.target.value })
					}
				/>
				{detail.data && detail.data.attendees.length > 0 ? (
					<div className={styles.attendees}>
						<h4>Attendees</h4>
						<ul>
							{detail.data.organizer ? (
								<li>
									{detail.data.organizer.name ?? detail.data.organizer.email}
									<Tag type="blue" size="sm">
										Organiser
									</Tag>
								</li>
							) : null}
							{detail.data.attendees.map((attendee) => (
								<li key={attendee.email}>
									{attendee.name ?? attendee.email}
									<Tag size="sm">
										{responseStatusLabel[attendee.responseStatus]}
									</Tag>
								</li>
							))}
						</ul>
						{!detail.data.isOrganizer ? (
							<div className={styles.rsvp}>
								<p>
									{detail.data.myResponseStatus === InviteResponse.Accept
										? "You accepted this invitation."
										: detail.data.myResponseStatus === InviteResponse.Decline
											? "You declined this invitation."
											: detail.data.myResponseStatus ===
												  InviteResponse.Tentative
												? "You responded tentatively."
												: "Respond to this invitation:"}
								</p>
								<TextInput
									id="event-rsvp-comment"
									labelText="Comment (optional)"
									value={comment}
									onChange={(event) => setComment(event.target.value)}
								/>
								<div className={styles.rsvpButtons}>
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
							</div>
						) : null}
					</div>
				) : null}
				{detail.data && detail.data.reminders.length > 0 ? (
					<div className={styles.reminders}>
						<h4>Reminders</h4>
						<ul>
							{detail.data.reminders.map((trigger) => (
								<li key={trigger}>{dayjs(trigger).format("MMM D, h:mm A")}</li>
							))}
						</ul>
					</div>
				) : null}
				{onDelete ? (
					<button
						type="button"
						className={styles.delete}
						onClick={() => {
							if (
								deletesWholeSeries &&
								!window.confirm(
									"This is a recurring event. Deleting it removes the entire series, not just this occurrence. Continue?",
								)
							) {
								return;
							}
							onDelete();
						}}
					>
						Delete event
					</button>
				) : null}
			</div>
		</Modal>
	);
}

// All-day values are parsed/formatted in UTC, never the viewer's local zone — see
// AllDayEventEnd.ts's doc comment: an all-day date is stored as literal-calendar-date UTC
// midnight, and formatting it in local time would shift the displayed date by one for any
// viewer west of UTC.
function toInputValue(iso: string, isAllDay: boolean): string {
	return isAllDay
		? dayjs.utc(iso).format("YYYY-MM-DD")
		: dayjs(iso).format("YYYY-MM-DDTHH:mm");
}

function fromInputValue(value: string, isAllDay: boolean): string {
	if (!value) return value;
	return isAllDay
		? dayjs.utc(value).startOf("day").toISOString()
		: dayjs(value).toISOString();
}
