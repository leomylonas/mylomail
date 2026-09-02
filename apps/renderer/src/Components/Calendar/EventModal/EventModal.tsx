import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Button, Modal, Tag, TextArea, TextInput, Toggle } from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import { InviteResponse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
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
	onSave,
	onDelete,
	onResolveConflict,
	onClose,
}: {
	hub: HubConnection;
	initial: EventFormValues;
	syncConflict?: boolean;
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

	const detail = useQuery({
		queryKey: ["calendar-event-detail", initial.eventId],
		queryFn: () =>
			hub.invoke<EventDetail>("GetCalendarEventDetail", initial.eventId),
		enabled: !isNew,
	});

	const respond = useMutation({
		mutationFn: (response: InviteResponse) =>
			hub.invoke("RespondToInvite", initial.eventId, response, comment || null),
		onSuccess: () => {
			setComment("");
			void queryClient.invalidateQueries({
				queryKey: ["calendar-event-detail", initial.eventId],
			});
		},
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
					onToggle={(checked) => setValues({ ...values, isAllDay: checked })}
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
						value={toInputValue(values.end, values.isAllDay)}
						onChange={(event) =>
							setValues({
								...values,
								end: fromInputValue(event.target.value, values.isAllDay),
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
					<button type="button" className={styles.delete} onClick={onDelete}>
						Delete event
					</button>
				) : null}
			</div>
		</Modal>
	);
}

function toInputValue(iso: string, isAllDay: boolean): string {
	const format = isAllDay ? "YYYY-MM-DD" : "YYYY-MM-DDTHH:mm";
	return dayjs(iso).format(format);
}

function fromInputValue(value: string, isAllDay: boolean): string {
	if (!value) return value;
	return isAllDay
		? dayjs(value).startOf("day").toISOString()
		: dayjs(value).toISOString();
}
