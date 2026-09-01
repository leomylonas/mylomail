import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import styles from "@mylomail/renderer/Components/Calendar/CalendarGrid/CalendarGrid.module.css";

export interface GridEvent {
	id: string;
	title: string;
	start: string;
	end: string;
	color: string;
	syncConflict: boolean;
}

const maxPerCell = 3;
const weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

/**
 * A month grid: six full weeks starting from the first visible day, so the layout never
 * reflows between a four-week and a six-week month.
 *
 * The day number and each event chip are separate buttons rather than one clickable cell —
 * a cell containing another interactive control cannot itself be a button, and giving the
 * cell a click handler with no keyboard equivalent is exactly what jsx-a11y's
 * `click-events-have-key-events` exists to catch (§15).
 */
export function CalendarGrid({
	anchor,
	events,
	onSelectDay,
	onSelectEvent,
}: {
	anchor: Dayjs;
	events: GridEvent[];
	onSelectDay: (date: Dayjs) => void;
	onSelectEvent: (eventId: string) => void;
}) {
	const gridStart = anchor.startOf("month").startOf("week");
	const days = Array.from({ length: 42 }, (_, i) => gridStart.add(i, "day"));
	const today = dayjs();

	return (
		<div className={styles.grid}>
			{weekdays.map((day) => (
				<div key={day} className={styles.weekday}>
					{day}
				</div>
			))}
			{days.map((day) => {
				const dayEvents = events.filter((event) => overlapsDay(event, day));
				const overflow = dayEvents.length - maxPerCell;

				return (
					<div
						key={day.toISOString()}
						className={styles.day}
						data-outside-month={day.month() !== anchor.month()}
						data-today={day.isSame(today, "day")}
					>
						<button
							type="button"
							className={styles.dayNumber}
							aria-label={`Add an event on ${day.format("MMMM D, YYYY")}`}
							onClick={() => onSelectDay(day)}
						>
							{day.date()}
						</button>
						<div className={styles.events}>
							{dayEvents.slice(0, maxPerCell).map((event) => (
								<button
									key={event.id}
									type="button"
									className={styles.eventChip}
									style={{ ["--mylomail-event-color" as string]: event.color }}
									data-conflict={event.syncConflict}
									onClick={() => onSelectEvent(event.id)}
								>
									{event.title || "(No title)"}
								</button>
							))}
							{overflow > 0 ? (
								<span className={styles.overflow}>+{overflow} more</span>
							) : null}
						</div>
					</div>
				);
			})}
		</div>
	);
}

function overlapsDay(event: GridEvent, day: Dayjs): boolean {
	const dayStart = day.startOf("day");
	const dayEnd = day.endOf("day");
	return (
		dayjs(event.start).isBefore(dayEnd) && dayjs(event.end).isAfter(dayStart)
	);
}
