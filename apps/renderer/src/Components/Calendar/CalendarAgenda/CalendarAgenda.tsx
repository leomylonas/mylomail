import { useEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	isRovingFocusKey,
	nextFocusIndex,
} from "@mylomail/renderer/Lib/RovingFocus";
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

	// Roving tabindex (§13), the same shape MessageList uses: the day rows are virtualized, but
	// keyboard focus moves between individual EVENTS, not days — a day with no events has
	// nothing to focus, and skipping straight past it is the only sensible behaviour. Flattening
	// every day's events into one ordered list gives `nextFocusIndex` the plain linear index it
	// expects; `dayIndex` on each entry is what actually drives `virtualizer.scrollToIndex`,
	// since that's the granularity react-virtual renders at.
	// Keyed by dayIndex+id, not id alone: a multi-day event legitimately appears once per day it
	// spans (buildDays puts it in every overlapping day), so bare event.id is not unique across
	// flatEvents — using it as a key would collide two different instances of the same event
	// onto one index/ref, breaking roving focus and imperative .focus() for whichever instance
	// lost the collision.
	const flatEvents = days.flatMap((day, dayIndex) =>
		day.events.map((event) => ({
			key: `${dayIndex}:${event.id}`,
			id: event.id,
			dayIndex,
		})),
	);
	// Built alongside flatEvents rather than looking up via findIndex per render (which would be
	// O(n) per event, O(n²) over the whole agenda) — cheap here since both are derived once per
	// render pass.
	const flatIndexByKey = new Map(
		flatEvents.map((entry, index) => [entry.key, index]),
	);
	const [focusedIndex, setFocusedIndex] = useState<number | null>(null);
	const rowRefs = useRef<Map<string, HTMLButtonElement>>(new Map());
	// Invalidates in-flight focusEventWhenReady retry chains from an earlier key press — see
	// MessageList.tsx's identical fix (pass 133): without this, holding an arrow key spawns one
	// independent rAF chain per keystroke, and whichever resolves last wins the DOM focus call
	// regardless of which key press it came from.
	const focusRequestId = useRef(0);

	// A stale index from the previous date range or event set means nothing once either
	// changes — the event it pointed to may no longer exist (a concurrent sync-driven
	// move/removal), or a different event may now occupy that slot. Depends on a string
	// signature of event ids, not `events` itself: the array (and the event objects within it)
	// get a fresh identity on every parent re-render regardless of whether the underlying data
	// actually changed (Calendar.tsx's `events` is built from `useQueries`, which — without a
	// `combine` option — returns a brand-new array every call), so depending on `events`
	// directly would clobber focus on every re-render the same way the unmemoized
	// rangeStart/rangeEnd did before that was fixed. A primitive string compares by value, so
	// this only actually changes when the set of event ids does.
	const eventIdsSignature = events.map((event) => event.id).join(",");
	useEffect(() => {
		setFocusedIndex(null);
	}, [rangeStart, rangeEnd, eventIdsSignature]);

	const renderedDayIndices = new Set(
		virtualizer.getVirtualItems().map((item) => item.index),
	);
	// Same fallback as MessageList: prefer the last focused event, but only among events whose
	// day is actually mounted — an index outside that set would leave nothing with tabIndex=0
	// at all, making the whole list untabbable-into until the next scroll.
	const tabbableIndex = (() => {
		const renderedEventIndices = flatEvents
			.map((_, index) => index)
			.filter((index) => renderedDayIndices.has(flatEvents[index].dayIndex));
		if (renderedEventIndices.length === 0) return null;
		const preferred = [focusedIndex, renderedEventIndices[0]];
		return (
			preferred.find(
				(index) => index !== null && renderedEventIndices.includes(index),
			) ?? renderedEventIndices[0]
		);
	})();

	function focusEventWhenReady(
		index: number,
		requestId: number,
		attemptsRemaining = 5,
	): void {
		if (focusRequestId.current !== requestId) return;
		const target = flatEvents[index];
		const element = target ? rowRefs.current.get(target.key) : undefined;
		if (element) {
			element.focus();
			return;
		}
		// The day row containing this event may not have mounted yet — react-virtual's
		// scroll-triggered re-render happens across frames, not synchronously with
		// scrollToIndex, so this retries on the next frame rather than depending on an effect
		// with no single dependency that reliably fires exactly when that render completes.
		if (attemptsRemaining > 0) {
			requestAnimationFrame(() =>
				focusEventWhenReady(index, requestId, attemptsRemaining - 1),
			);
		}
	}

	function moveRovingFocus(
		key: "ArrowUp" | "ArrowDown" | "Home" | "End",
		currentIndex: number,
	): void {
		const next = nextFocusIndex(key, currentIndex, flatEvents.length);
		if (next === currentIndex || flatEvents.length === 0) return;
		setFocusedIndex(next);
		virtualizer.scrollToIndex(flatEvents[next].dayIndex, { align: "auto" });
		focusRequestId.current += 1;
		focusEventWhenReady(next, focusRequestId.current);
	}

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
									{day.events.map((event) => {
										const rowKey = `${item.index}:${event.id}`;
										const flatIndex = flatIndexByKey.get(rowKey) ?? -1;
										return (
											<li key={event.id}>
												<button
													type="button"
													ref={(element) => {
														if (element) rowRefs.current.set(rowKey, element);
														else rowRefs.current.delete(rowKey);
													}}
													tabIndex={flatIndex === tabbableIndex ? 0 : -1}
													className={styles.event}
													style={{
														["--mylomail-event-color" as string]: event.color,
													}}
													data-conflict={event.syncConflict}
													onKeyDown={(keyEvent) => {
														if (!isRovingFocusKey(keyEvent.key)) return;
														keyEvent.preventDefault();
														moveRovingFocus(keyEvent.key, flatIndex);
													}}
													onClick={() => {
														setFocusedIndex(flatIndex);
														onSelectEvent(event.id);
													}}
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
										);
									})}
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
