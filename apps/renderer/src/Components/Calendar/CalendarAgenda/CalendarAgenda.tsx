import { useRef } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import styles from "@mylomail/renderer/Components/Calendar/CalendarAgenda/CalendarAgenda.module.css";

export interface AgendaEvent {
	id: string;
	title: string;
	location: string | null;
	start: string;
	end: string;
	isAllDay: boolean;
	color: string;
	syncConflict: boolean;
}

interface AgendaDay {
	date: Dayjs;
	events: AgendaEvent[];
}

/**
 * The list view, one row per day in range. Virtualised because a wide range across several
 * accounts can hold far more days than are ever on screen at once (§13 Epic 7); row height is
 * measured rather than estimated, since a day with no events and a day with six of them are
 * not the same height.
 */
export function CalendarAgenda({
	rangeStart,
	rangeEnd,
	events,
	onSelectEvent,
}: {
	rangeStart: Dayjs;
	rangeEnd: Dayjs;
	events: AgendaEvent[];
	onSelectEvent: (eventId: string) => void;
}) {
	const parentRef = useRef<HTMLDivElement>(null);
	const days = buildDays(rangeStart, rangeEnd, events);

	const virtualizer = useVirtualizer({
		count: days.length,
		getScrollElement: () => parentRef.current,
		estimateSize: () => 72,
		overscan: 8,
	});

	return (
		<div ref={parentRef} className={styles.scroller}>
			<div
				className={styles.spacer}
				style={{ height: virtualizer.getTotalSize() }}
			>
				{virtualizer.getVirtualItems().map((item) => {
					const day = days[item.index];
					return (
						<div
							key={day.date.toISOString()}
							ref={virtualizer.measureElement}
							data-index={item.index}
							className={styles.dayRow}
							style={{
								transform: `translateY(${item.start}px)`,
							}}
						>
							<div className={styles.dayLabel}>
								{day.date.format("ddd, MMM D")}
							</div>
							{day.events.length === 0 ? (
								<p className={styles.empty}>No events.</p>
							) : (
								<ul className={styles.eventList}>
									{day.events.map((event) => (
										<li key={event.id}>
											<button
												type="button"
												className={styles.event}
												style={{
													["--mylomail-event-color" as string]: event.color,
												}}
												data-conflict={event.syncConflict}
												onClick={() => onSelectEvent(event.id)}
											>
												<span className={styles.time}>
													{event.isAllDay
														? "All day"
														: dayjs(event.start).format("h:mm A")}
												</span>
												<span
													className={styles.title}
													title={event.title || "(No title)"}
												>
													{event.title || "(No title)"}
												</span>
												{event.location ? (
													<span
														className={styles.location}
														title={event.location}
													>
														{event.location}
													</span>
												) : null}
											</button>
										</li>
									))}
								</ul>
							)}
						</div>
					);
				})}
			</div>
		</div>
	);
}

function buildDays(
	rangeStart: Dayjs,
	rangeEnd: Dayjs,
	events: AgendaEvent[],
): AgendaDay[] {
	const days: AgendaDay[] = [];
	let cursor = rangeStart.startOf("day");
	const end = rangeEnd.endOf("day");

	while (cursor.isBefore(end)) {
		const dayStart = cursor;
		const dayEnd = cursor.endOf("day");
		days.push({
			date: cursor,
			events: events
				.filter(
					(event) =>
						dayjs(event.start).isBefore(dayEnd) &&
						dayjs(event.end).isAfter(dayStart),
				)
				.sort((a, b) => dayjs(a.start).valueOf() - dayjs(b.start).valueOf()),
		});
		cursor = cursor.add(1, "day");
	}

	return days;
}
