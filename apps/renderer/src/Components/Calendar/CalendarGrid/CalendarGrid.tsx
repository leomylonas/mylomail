import { useRef, useState } from "react";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	calendarFormatters,
	calendarGridStart,
} from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
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

export function calendarDayFocusTarget(
	index: number,
	key: string,
	dayCount: number,
): number | null {
	const target =
		key === "ArrowLeft"
			? index - 1
			: key === "ArrowRight"
				? index + 1
				: key === "ArrowUp"
					? index - 7
					: key === "ArrowDown"
						? index + 7
						: key === "Home"
							? Math.floor(index / 7) * 7
							: key === "End"
								? Math.floor(index / 7) * 7 + 6
								: null;
	return target === null || target < 0 || target >= dayCount ? null : target;
}

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
	const gridStart = calendarGridStart(anchor);
	const days = Array.from({ length: 42 }, (_, i) => gridStart.add(i, "day"));
	const weeks = Array.from({ length: 6 }, (_, weekIndex) =>
		days.slice(weekIndex * 7, (weekIndex + 1) * 7),
	);
	const [focusedDayIndex, setFocusedDayIndex] = useState(0);
	const dayButtons = useRef<Array<HTMLButtonElement | null>>([]);

	function moveDayFocus(index: number, key: string): void {
		const target = calendarDayFocusTarget(index, key, days.length);
		if (target === null) return;

		setFocusedDayIndex(target);
		requestAnimationFrame(() => dayButtons.current[target]?.focus());
	}
	const today = dayjs();

	return (
		<div
			className={styles.grid}
			role="grid"
			aria-label={`Calendar for ${calendarFormatters.monthYear(anchor.toDate())}`}
		>
			<div className={styles.row} role="row">
				{days.slice(0, 7).map((day) => (
					<div
						key={day.toISOString()}
						className={styles.weekday}
						role="columnheader"
					>
						{calendarFormatters.weekdayShort(day.toDate())}
					</div>
				))}
			</div>
			{weeks.map((week, weekIndex) => (
				<div key={week[0].toISOString()} className={styles.row} role="row">
					{week.map((day, dayIndex) => {
						const index = weekIndex * 7 + dayIndex;
						const dayEvents = events.filter((event) => overlapsDay(event, day));
						const overflow = dayEvents.length - maxPerCell;

						return (
							<div
								key={day.toISOString()}
								className={styles.day}
								role="gridcell"
								aria-colindex={dayIndex + 1}
								aria-label={calendarFormatters.fullDate(day.toDate())}
								data-outside-month={day.month() !== anchor.month()}
								data-today={day.isSame(today, "day")}
							>
								<button
									type="button"
									ref={(element) => {
										dayButtons.current[index] = element;
									}}
									tabIndex={index === focusedDayIndex ? 0 : -1}
									className={styles.dayNumber}
									aria-label={`Add an event on ${calendarFormatters.fullDate(day.toDate())}`}
									onKeyDown={(event) => {
										if (
											![
												"ArrowLeft",
												"ArrowRight",
												"ArrowUp",
												"ArrowDown",
												"Home",
												"End",
											].includes(event.key)
										)
											return;
										event.preventDefault();
										moveDayFocus(index, event.key);
									}}
									onClick={() => {
										setFocusedDayIndex(index);
										onSelectDay(day);
									}}
								>
									{calendarFormatters.dayNumber(day.toDate())}
								</button>
								<div className={styles.events}>
									{dayEvents.slice(0, maxPerCell).map((event) => (
										<button
											key={event.id}
											type="button"
											className={styles.eventChip}
											style={{
												["--mylomail-event-color" as string]: event.color,
											}}
											data-conflict={event.syncConflict}
											aria-label={`${event.title || "(No title)"}, ${calendarFormatters.monthDay(day.toDate())}`}
											onClick={() => onSelectEvent(event.id)}
										>
											{event.title || "(No title)"}
										</button>
									))}
									{overflow > 0 ? (
										<span className={styles.overflow}>
											+{calendarFormatters.number(overflow)} more
										</span>
									) : null}
								</div>
							</div>
						);
					})}
				</div>
			))}
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
