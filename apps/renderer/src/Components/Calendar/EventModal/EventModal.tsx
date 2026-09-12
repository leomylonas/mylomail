import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
	Button,
	Modal,
	Select,
	SelectItem,
	Tag,
	TextArea,
	TextInput,
	Toggle,
} from "@carbon/react";
import type { HubConnection } from "@microsoft/signalr";
import { InviteResponse } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import { dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import {
	fromInclusiveEndDateInputValue,
	toInclusiveEndDateInputValue,
} from "@mylomail/renderer/Components/Calendar/EventModal/AllDayEventEnd";
import {
	calendarTimeZones,
	defaultCalendarTimeZone,
	formatRecurrenceDateLines,
	fromZonedDateTimeInputValue,
	parseRecurrenceDateLines,
	rezoneInstant,
	toZonedDateTimeInputValue,
	recurrenceRuleForPreset,
} from "@mylomail/renderer/Components/Calendar/EventModal/EventScheduling";
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
	startTimeZoneId: string | null;
	endTimeZoneId: string | null;
	recurrenceRules: string[];
	recurrenceDates: string[];
	exceptionDates: string[];
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
	startTimeZoneId: string;
	endTimeZoneId: string;
	recurrenceRulesText: string;
	recurrenceDatesText: string;
	exceptionDatesText: string;
}

/**
 * Create or edit one event, including the full RFC 5545 recurrence set. Common recurrence
 * frequencies have a quick selector; the lossless rule/date fields remain available because
 * RRULE + RDATE + EXDATE cannot be represented by one preset without discarding information.
 */
export function EventModal({
	hub,
	initial,
	syncConflict,
	deletesWholeSeries,
	recurrenceEditable = true,
	supportsRecurrenceSets = true,
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
	recurrenceEditable?: boolean;
	supportsRecurrenceSets?: boolean;
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
	const isNew = !initial.eventId;
	const [values, setValues] = useState(initial);
	const [comment, setComment] = useState("");
	const [timeError, setTimeError] = useState<string | null>(null);
	const [appliedDetail, setAppliedDetail] = useState<EventDetail | null>(null);
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	const detail = useQuery({
		queryKey: ["calendar-event-detail", initial.eventId],
		queryFn: () =>
			hub.invoke<EventDetail>("GetCalendarEventDetail", initial.eventId),
		enabled: !isNew,
		staleTime: 0,
		refetchOnMount: "always",
	});

	// Existing-event fields are withheld until a forced fresh detail read completes. A guarded
	// render-time adjustment is deliberate here: the form is not mounted yet, so no user edit
	// can be overwritten, and React immediately restarts this render with one coherent snapshot.
	if (
		!isNew &&
		appliedDetail === null &&
		!detail.isFetching &&
		detail.isSuccess
	) {
		const startTimeZoneId =
			detail.data.startTimeZoneId ?? defaultCalendarTimeZone;
		const endTimeZoneId = detail.data.endTimeZoneId ?? startTimeZoneId;
		setValues((current) => ({
			...current,
			start: detail.data.start,
			end: detail.data.end,
			isAllDay: detail.data.isAllDay,
			startTimeZoneId,
			endTimeZoneId,
			recurrenceRulesText: detail.data.recurrenceRules.join("\n"),
			recurrenceDatesText: formatRecurrenceDateLines(
				detail.data.recurrenceDates,
				startTimeZoneId,
				detail.data.isAllDay,
			),
			exceptionDatesText: formatRecurrenceDateLines(
				detail.data.exceptionDates,
				startTimeZoneId,
				detail.data.isAllDay,
			),
		}));
		setAppliedDetail(detail.data);
	}

	const detailLoaded = isNew || appliedDetail !== null;

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

	const selectableTimeZones = [
		...new Set([
			...calendarTimeZones,
			values.startTimeZoneId,
			values.endTimeZoneId,
		]),
	].sort((left, right) => left.localeCompare(right));
	const recurrenceError = recurrenceValidationError(
		values,
		timeError,
		supportsRecurrenceSets,
	);
	const recurrencePreset = recurrencePresetOf(values);

	function updateTimedInstant(
		field: "start" | "end",
		wallTime: string,
		timeZoneId: string,
	) {
		try {
			const instant = fromZonedDateTimeInputValue(wallTime, timeZoneId);
			setValues((current) => ({ ...current, [field]: instant }));
			setTimeError(null);
		} catch (error) {
			setTimeError(error instanceof Error ? error.message : String(error));
		}
	}

	if (!detailLoaded) {
		const failed = detail.isError && !detail.isFetching;
		return (
			<Modal
				open
				modalHeading="Edit event"
				primaryButtonText="Save"
				secondaryButtonText="Cancel"
				onRequestClose={onClose}
				onRequestSubmit={() => undefined}
				primaryButtonDisabled
			>
				<div className={styles.form}>
					<p role={failed ? "alert" : "status"}>
						{failed
							? "The latest event details could not be loaded."
							: "Loading the latest event details…"}
					</p>
					{failed ? (
						<Button
							size="sm"
							kind="tertiary"
							onClick={() => void detail.refetch()}
						>
							Retry
						</Button>
					) : null}
				</div>
			</Modal>
		);
	}

	return (
		<Modal
			open
			modalHeading={isNew ? "New event" : "Edit event"}
			primaryButtonText="Save"
			secondaryButtonText="Cancel"
			onRequestClose={onClose}
			onRequestSubmit={() => onSave(values)}
			danger={false}
			primaryButtonDisabled={
				(!isNew && !detail.data) || recurrenceError !== null
			}
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
					onToggle={(checked) => {
						setTimeError(null);
						setValues(toggleAllDay(values, checked));
					}}
				/>
				<div className={styles.row}>
					<TextInput
						id="event-start"
						labelText="Start"
						type={values.isAllDay ? "date" : "datetime-local"}
						step={values.isAllDay ? undefined : 1}
						value={toInputValue(
							values.start,
							values.isAllDay,
							values.startTimeZoneId,
						)}
						onChange={(event) => {
							if (values.isAllDay) {
								setTimeError(null);
								setValues({
									...values,
									start: fromInputValue(
										event.target.value,
										true,
										values.startTimeZoneId,
									),
								});
							} else {
								updateTimedInstant(
									"start",
									event.target.value,
									values.startTimeZoneId,
								);
							}
						}}
					/>
					<TextInput
						id="event-end"
						labelText="End"
						type={values.isAllDay ? "date" : "datetime-local"}
						step={values.isAllDay ? undefined : 1}
						value={
							values.isAllDay
								? toInclusiveEndDateInputValue(values.end)
								: toInputValue(values.end, false, values.endTimeZoneId)
						}
						onChange={(event) => {
							if (values.isAllDay) {
								setTimeError(null);
								setValues({
									...values,
									end: fromInclusiveEndDateInputValue(event.target.value),
								});
							} else {
								updateTimedInstant(
									"end",
									event.target.value,
									values.endTimeZoneId,
								);
							}
						}}
					/>
				</div>
				{!values.isAllDay ? (
					<div className={styles.row}>
						<Select
							id="event-start-time-zone"
							labelText="Start time zone"
							value={values.startTimeZoneId}
							onChange={(event) => {
								const next = event.target.value;
								try {
									const start = rezoneInstant(
										values.start,
										values.startTimeZoneId,
										next,
									);
									const sharedZone =
										values.endTimeZoneId === values.startTimeZoneId;
									setValues({
										...values,
										start,
										end: sharedZone
											? rezoneInstant(values.end, values.endTimeZoneId, next)
											: values.end,
										startTimeZoneId: next,
										endTimeZoneId: sharedZone ? next : values.endTimeZoneId,
									});
									setTimeError(null);
								} catch (error) {
									setTimeError(
										error instanceof Error ? error.message : String(error),
									);
								}
							}}
						>
							{selectableTimeZones.map((timeZone) => (
								<SelectItem key={timeZone} value={timeZone} text={timeZone} />
							))}
						</Select>
						<Select
							id="event-end-time-zone"
							labelText="End time zone"
							value={values.endTimeZoneId}
							onChange={(event) => {
								const next = event.target.value;
								try {
									setValues({
										...values,
										end: rezoneInstant(values.end, values.endTimeZoneId, next),
										endTimeZoneId: next,
									});
									setTimeError(null);
								} catch (error) {
									setTimeError(
										error instanceof Error ? error.message : String(error),
									);
								}
							}}
						>
							{selectableTimeZones.map((timeZone) => (
								<SelectItem key={timeZone} value={timeZone} text={timeZone} />
							))}
						</Select>
					</div>
				) : null}
				{recurrenceEditable ? (
					<>
						<Select
							id="event-repeat"
							labelText="Repeat"
							value={recurrencePreset}
							onChange={(event) => {
								const preset = event.target.value;
								setValues({
									...values,
									recurrenceRulesText:
										preset === "none"
											? ""
											: preset === "custom"
												? values.recurrenceRulesText ||
													recurrenceRuleForPreset(
														"weekly",
														values.start,
														values.startTimeZoneId,
													)
												: recurrenceRuleForPreset(
														preset as "daily" | "weekly" | "monthly" | "yearly",
														values.start,
														values.startTimeZoneId,
													),
									recurrenceDatesText:
										preset === "none" || !supportsRecurrenceSets
											? ""
											: values.recurrenceDatesText,
									exceptionDatesText:
										preset === "none" || !supportsRecurrenceSets
											? ""
											: values.exceptionDatesText,
								});
							}}
						>
							<SelectItem value="none" text="Does not repeat" />
							<SelectItem value="daily" text="Daily" />
							<SelectItem value="weekly" text="Weekly" />
							<SelectItem value="monthly" text="Monthly" />
							<SelectItem value="yearly" text="Yearly" />
							<SelectItem
								value="custom"
								text={
									supportsRecurrenceSets
										? "Custom recurrence set"
										: "Current Microsoft 365 pattern"
								}
								disabled={!supportsRecurrenceSets}
							/>
						</Select>
						{recurrencePreset !== "none" && supportsRecurrenceSets ? (
							<div className={styles.recurrence}>
								<TextArea
									id="event-recurrence-rules"
									labelText="Recurrence rules"
									helperText="One RFC 5545 RRULE per line, without the RRULE: prefix."
									value={values.recurrenceRulesText}
									onChange={(event) =>
										setValues({
											...values,
											recurrenceRulesText: event.target.value,
										})
									}
								/>
								<TextArea
									id="event-recurrence-dates"
									labelText="Additional occurrence dates"
									helperText={
										values.isAllDay
											? "One date per line (YYYY-MM-DD)."
											: "One local date/time per line (YYYY-MM-DDTHH:mm with optional seconds and milliseconds), interpreted in the start time zone."
									}
									value={values.recurrenceDatesText}
									onChange={(event) =>
										setValues({
											...values,
											recurrenceDatesText: event.target.value,
										})
									}
								/>
								<TextArea
									id="event-exception-dates"
									labelText="Excluded occurrence dates"
									helperText={
										values.isAllDay
											? "One date per line (YYYY-MM-DD)."
											: "One local date/time per line (YYYY-MM-DDTHH:mm with optional seconds and milliseconds), interpreted in the start time zone."
									}
									value={values.exceptionDatesText}
									onChange={(event) =>
										setValues({
											...values,
											exceptionDatesText: event.target.value,
										})
									}
								/>
							</div>
						) : recurrencePreset !== "none" ? (
							<p>
								Microsoft 365 supports one recurrence pattern. Choose a common
								repeat option to replace the current pattern.
							</p>
						) : null}
					</>
				) : (
					<p>
						Recurrence settings belong to the series and cannot be changed on
						one override.
					</p>
				)}
				{recurrenceError ? (
					<p className={styles.validation} role="alert">
						{recurrenceError}
					</p>
				) : null}
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
function toInputValue(
	iso: string,
	isAllDay: boolean,
	timeZoneId: string,
): string {
	return isAllDay
		? dayjs.utc(iso).format("YYYY-MM-DD")
		: toZonedDateTimeInputValue(iso, timeZoneId);
}

function fromInputValue(
	value: string,
	isAllDay: boolean,
	timeZoneId: string,
): string {
	if (!value) return value;
	return isAllDay
		? dayjs.utc(value).startOf("day").toISOString()
		: fromZonedDateTimeInputValue(value, timeZoneId);
}

function recurrencePresetOf(values: EventFormValues): string {
	if (values.recurrenceDatesText.trim() || values.exceptionDatesText.trim()) {
		return "custom";
	}
	const rules = values.recurrenceRulesText
		.split(/\r?\n/u)
		.map((rule) => rule.trim().replace(/^RRULE:/iu, ""))
		.filter(Boolean);
	if (rules.length === 0) return "none";
	if (rules.length !== 1) return "custom";
	for (const preset of ["daily", "weekly", "monthly", "yearly"] as const) {
		if (
			rules[0].toUpperCase() ===
			recurrenceRuleForPreset(preset, values.start, values.startTimeZoneId)
		) {
			return preset;
		}
	}
	return "custom";
}

function recurrenceValidationError(
	values: EventFormValues,
	timeError: string | null,
	supportsRecurrenceSets: boolean,
): string | null {
	if (timeError) return timeError;
	if (!dayjs(values.start).isValid() || !dayjs(values.end).isValid()) {
		return "Start and end must be valid dates.";
	}
	if (dayjs(values.end).isBefore(dayjs(values.start))) {
		return "End must not be before start.";
	}
	if (
		!supportsRecurrenceSets &&
		(values.recurrenceDatesText.trim() || values.exceptionDatesText.trim())
	) {
		return "Microsoft 365 does not support additional or excluded recurrence dates.";
	}
	try {
		parseRecurrenceDateLines(
			values.recurrenceDatesText,
			values.startTimeZoneId,
			values.isAllDay,
		);
		parseRecurrenceDateLines(
			values.exceptionDatesText,
			values.startTimeZoneId,
			values.isAllDay,
		);
		return null;
	} catch (error) {
		return error instanceof Error ? error.message : String(error);
	}
}

function toggleAllDay(
	values: EventFormValues,
	isAllDay: boolean,
): EventFormValues {
	if (isAllDay) {
		const startDate = toZonedDateTimeInputValue(
			values.start,
			values.startTimeZoneId,
		).slice(0, 10);
		const start = dayjs.utc(startDate).startOf("day");
		return {
			...values,
			isAllDay: true,
			start: start.toISOString(),
			end: start.add(1, "day").toISOString(),
			recurrenceDatesText: values.recurrenceDatesText.replace(
				/^(\d{4}-\d{2}-\d{2})T.*$/gmu,
				"$1",
			),
			exceptionDatesText: values.exceptionDatesText.replace(
				/^(\d{4}-\d{2}-\d{2})T.*$/gmu,
				"$1",
			),
		};
	}

	const date = dayjs.utc(values.start).format("YYYY-MM-DD");
	const start = fromZonedDateTimeInputValue(
		`${date}T09:00`,
		values.startTimeZoneId,
	);
	return {
		...values,
		isAllDay: false,
		start,
		end: dayjs(start).add(1, "hour").toISOString(),
		recurrenceDatesText: values.recurrenceDatesText.replace(
			/^(\d{4}-\d{2}-\d{2})$/gmu,
			"$1T09:00",
		),
		exceptionDatesText: values.exceptionDatesText.replace(
			/^(\d{4}-\d{2}-\d{2})$/gmu,
			"$1T09:00",
		),
	};
}
