import { useState } from "react";
import { Modal, TextArea, TextInput, Toggle } from "@carbon/react";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import styles from "@mylomail/renderer/Components/Calendar/EventModal/EventModal.module.css";

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
	initial,
	syncConflict,
	onSave,
	onDelete,
	onClose,
}: {
	initial: EventFormValues;
	syncConflict?: boolean;
	onSave: (values: EventFormValues) => void;
	onDelete?: () => void;
	onClose: () => void;
}) {
	const [values, setValues] = useState(initial);
	const isNew = !initial.eventId;

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
					<p className={styles.conflict}>
						The server&apos;s copy changed since this was last read. Saving will
						overwrite it with what you see here.
					</p>
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
