import {
	useCallback,
	useEffect,
	useLayoutEffect,
	useRef,
	useState,
	type PointerEvent as ReactPointerEvent,
} from "react";
import { IconButton } from "@carbon/react";
import { ZoomFit, ZoomIn, ZoomOut } from "@carbon/icons-react";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import { calendarFormatters } from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import { readableTextColour } from "@mylomail/renderer/Components/Calendar/CalendarColour";
import {
	eventTitle,
	locationText,
	timeRangeLabel,
} from "@mylomail/renderer/Components/Calendar/CalendarEventText";
import {
	allDayColumns,
	allDayStrip,
	blockContent,
	initialScrollMinute,
	layoutLanes,
	minutesPerDay,
	packAllDayRows,
	slotStart,
	timedBlockInterval,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekLayout";
import {
	anchoredScrollTop,
	clampHourHeight,
	defaultHourHeight,
	fitHourHeight,
	hoursPerDay,
	labelStepHours,
	maxHourHeight,
	minimumBlockMinutesFor,
	minorGridlines,
	snapMinutes,
	wheelZoomFactor,
	zoomStepFactor,
	zoomedHourHeight,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekZoom";
import {
	autoScrollDelta,
	dragThreshold,
	keyboardRange,
	rangeDates,
	rangeSegments,
	selectionFromDrag,
	type GridCell,
	type GridMetrics,
	type GridPoint,
	type TimeRange,
} from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeekSelection";
import styles from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeek.module.css";

export interface WeekEvent {
	id: string;
	title: string;
	location: string | null;
	start: string;
	end: string;
	isAllDay: boolean;
	color: string;
	syncConflict: boolean;
}

const dayCount = 7;
const hours = Array.from({ length: hoursPerDay }, (_, hour) => hour);

interface DragState {
	pointerId: number;
	/** Where the press began, in grid coordinates — fixed to the day, not the scroll position. */
	origin: GridPoint;
	/** Set once the pointer has moved past the click threshold. */
	started: boolean;
	latestClient: GridPoint;
	latestRange: TimeRange | null;
}

function percentOfDay(minutes: number): string {
	return `${(minutes / minutesPerDay) * 100}%`;
}

function clamp(value: number, low: number, high: number): number {
	return Math.min(Math.max(value, low), high);
}

/**
 * A week as a time grid (§13 Epic 7): seven day columns and a row per hour, an all-day strip
 * under the sticky day headers, timed events as blocks placed by their start and end, and a
 * line for the current time on today's column.
 *
 * The hour height is the zoom. Ctrl/Cmd + wheel (a trackpad pinch arrives the same way) zooms
 * continuously about the pointer, the toolbar buttons and `+` / `-` / `0` zoom in steps about
 * the keyboard cursor, and the lowest zoom fits all 24 hours in the view with no scrollbar.
 * Everything positioned inside a column is a percentage of its 24-hour height, so blocks, the
 * now-line and selections follow the zoom without being recalculated.
 *
 * Pointer events with capture on the grid drive range selection: a press that moves past a few
 * pixels becomes a drag across columns, snapped to the zoom's step; it keeps tracking outside
 * the viewport, auto-scrolls near the top and bottom edges, and Escape cancels it. A press that
 * doesn't move is left alone so click and double-click behave as usual. The columns are the
 * keyboard surface: one tab stop, arrows move a slot cursor, Shift+arrows extend a range, and
 * Enter creates an event at the cursor or over the range.
 */
export function CalendarWeek({
	weekStart,
	events,
	onSelectEvent,
	onCreateAt,
	onCreateRange,
}: {
	weekStart: Dayjs;
	events: WeekEvent[];
	onSelectEvent: (eventId: string) => void;
	/** A new event was asked for at a single moment: it starts at `start`. */
	onCreateAt: (start: Dayjs) => void;
	/** A range was selected: a new event spans `start` to `end`, possibly over several days. */
	onCreateRange: (start: Dayjs, end: Dayjs) => void;
}) {
	const days = Array.from({ length: dayCount }, (_, index) =>
		weekStart.add(index, "day"),
	);
	const [now, setNow] = useState(() => dayjs());
	const [initialMinute] = useState(() =>
		initialScrollMinute(weekStart, dayjs()),
	);
	// `null` is "fit the day"; a number is a chosen hour height. This window's own state.
	const [zoom, setZoom] = useState<number | null>(defaultHourHeight);
	const [fit, setFit] = useState(() => fitHourHeight(600));
	const [measured, setMeasured] = useState(false);
	const [columnWidth, setColumnWidth] = useState(120);
	const [allDayExpanded, setAllDayExpanded] = useState(false);
	const [cursor, setCursor] = useState<GridCell>(() => ({
		column: 0,
		minute: Math.floor(initialMinute / 30) * 30,
	}));
	const [rangeAnchor, setRangeAnchor] = useState<GridCell | null>(null);
	const [drag, setDrag] = useState<{
		range: TimeRange;
		client: GridPoint;
	} | null>(null);

	const hourHeight = clampHourHeight(zoom ?? fit, fit);
	const step = snapMinutes(hourHeight);
	const cursorMinute = Math.floor(cursor.minute / step) * step;

	const scrollerRef = useRef<HTMLDivElement>(null);
	const headRef = useRef<HTMLDivElement>(null);
	const bodyRef = useRef<HTMLDivElement>(null);
	const columnRefs = useRef<Array<HTMLDivElement | null>>([]);
	// The numbers event handlers need that must be current between renders — successive wheel
	// events can arrive faster than React commits a zoom.
	const live = useRef({ hourHeight, fit, viewportHeight: 0 });
	const pendingScrollTop = useRef<number | null>(null);
	const dragState = useRef<DragState | null>(null);
	const didInitialScroll = useRef(false);

	useLayoutEffect(() => {
		live.current.hourHeight = hourHeight;
		live.current.fit = fit;
		// A zoom changes the content height in this commit; the scroll that keeps the
		// anchored moment in place can only be applied once that height exists.
		const scroller = scrollerRef.current;
		if (scroller && pendingScrollTop.current !== null) {
			scroller.scrollTop = pendingScrollTop.current;
			pendingScrollTop.current = null;
		}
	}, [hourHeight, fit]);

	// The room the 24 hours have is the scroller minus the sticky header (day names and the
	// all-day strip, whose height changes with its rows), so both are observed.
	useLayoutEffect(() => {
		const scroller = scrollerRef.current;
		const head = headRef.current;
		if (!scroller || !head) return;

		const observer = new ResizeObserver(() => {
			const viewportHeight = scroller.clientHeight - head.offsetHeight;
			live.current.viewportHeight = viewportHeight;
			setFit(fitHourHeight(viewportHeight));
			const column = columnRefs.current[0];
			if (column) setColumnWidth(column.offsetWidth);
			setMeasured(true);
		});
		observer.observe(scroller);
		observer.observe(head);
		return () => observer.disconnect();
	}, []);

	// Opens near the current time (or the start of the working day) rather than at midnight.
	useLayoutEffect(() => {
		const scroller = scrollerRef.current;
		if (!measured || didInitialScroll.current || !scroller) return;
		didInitialScroll.current = true;
		scroller.scrollTop = (initialMinute / 60) * hourHeight;
	}, [measured, hourHeight, initialMinute]);

	useEffect(() => {
		const timer = window.setInterval(() => setNow(dayjs()), 60_000);
		return () => window.clearInterval(timer);
	}, []);

	/** Zoom by `factor` keeping the moment `anchorOffset` pixels below the grid's top still there. */
	const zoomBy = useCallback((factor: number, anchorOffset: number) => {
		const scroller = scrollerRef.current;
		if (!scroller) return;

		const { hourHeight: current, fit: lowest, viewportHeight } = live.current;
		const next = zoomedHourHeight(current, factor, lowest);
		if (next === current) return;

		pendingScrollTop.current = anchoredScrollTop({
			scrollTop: pendingScrollTop.current ?? scroller.scrollTop,
			anchorOffset: clamp(anchorOffset, 0, viewportHeight),
			oldHourHeight: current,
			newHourHeight: next,
			viewportHeight,
		});
		live.current.hourHeight = next;
		setZoom(next <= lowest ? null : next);
	}, []);

	const fitDay = useCallback(() => {
		const scroller = scrollerRef.current;
		if (!scroller) return;

		if (live.current.hourHeight === live.current.fit) {
			scroller.scrollTop = 0;
		} else {
			pendingScrollTop.current = 0;
			live.current.hourHeight = live.current.fit;
		}
		setZoom(null);
	}, []);

	// React attaches wheel listeners as passive, which cannot stop the page from zooming or
	// scrolling, so this one is added by hand. Plain wheel is left alone and scrolls.
	useEffect(() => {
		const scroller = scrollerRef.current;
		const head = headRef.current;
		if (!scroller || !head) return;

		const onWheel = (event: WheelEvent) => {
			if (!event.ctrlKey && !event.metaKey) return;
			event.preventDefault();
			const gridTop = scroller.getBoundingClientRect().top + head.offsetHeight;
			zoomBy(
				wheelZoomFactor(event.deltaY, event.deltaMode),
				event.clientY - gridTop,
			);
		};
		scroller.addEventListener("wheel", onWheel, { passive: false });
		return () => scroller.removeEventListener("wheel", onWheel);
	}, [zoomBy]);

	/** The grid's origin on screen and its current scale, read fresh at each use. */
	const readGrid = useCallback((): {
		left: number;
		top: number;
		metrics: GridMetrics;
	} | null => {
		const body = bodyRef.current;
		const first = columnRefs.current[0];
		if (!body || !first) return null;

		const firstBox = first.getBoundingClientRect();
		return {
			left: firstBox.left,
			top: body.getBoundingClientRect().top,
			metrics: {
				columnWidth: firstBox.width,
				hourHeight: live.current.hourHeight,
				columnCount: dayCount,
			},
		};
	}, []);

	const updateDrag = useCallback(
		(client: GridPoint) => {
			const state = dragState.current;
			const grid = readGrid();
			if (!state || !grid) return;

			state.latestClient = client;
			const range = selectionFromDrag(
				state.origin,
				{ x: client.x - grid.left, y: client.y - grid.top },
				grid.metrics,
				snapMinutes(grid.metrics.hourHeight),
				state.started ? 0 : dragThreshold,
			);
			if (!range) return;

			state.started = true;
			state.latestRange = range;
			setDrag({ range, client });
		},
		[readGrid],
	);

	const cancelDrag = useCallback(() => {
		const state = dragState.current;
		dragState.current = null;
		setDrag(null);
		const body = bodyRef.current;
		if (state && body?.hasPointerCapture(state.pointerId)) {
			body.releasePointerCapture(state.pointerId);
		}
	}, []);

	const dragging = drag !== null;
	useEffect(() => {
		if (!dragging) return;

		const onKeyDown = (event: KeyboardEvent) => {
			if (event.key !== "Escape") return;
			event.preventDefault();
			cancelDrag();
		};
		window.addEventListener("keydown", onKeyDown);

		// Near the top or bottom edge the grid keeps scrolling while the pointer is held still,
		// and the selection is recomputed against the new scroll position each time.
		let frame = 0;
		const tick = () => {
			const state = dragState.current;
			const scroller = scrollerRef.current;
			const head = headRef.current;
			if (state?.started && scroller && head) {
				const box = scroller.getBoundingClientRect();
				const delta = autoScrollDelta(
					state.latestClient.y,
					box.top + head.offsetHeight,
					box.bottom,
				);
				if (delta !== 0) {
					const before = scroller.scrollTop;
					scroller.scrollTop += delta;
					if (scroller.scrollTop !== before) updateDrag(state.latestClient);
				}
			}
			frame = requestAnimationFrame(tick);
		};
		frame = requestAnimationFrame(tick);

		return () => {
			window.removeEventListener("keydown", onKeyDown);
			cancelAnimationFrame(frame);
		};
	}, [dragging, cancelDrag, updateDrag]);

	function handlePointerDown(event: ReactPointerEvent<HTMLDivElement>): void {
		// Touch is left to scroll the grid; a block opens its event; only the primary button.
		if (event.button !== 0 || event.pointerType === "touch") return;
		if ((event.target as HTMLElement).closest("button")) return;

		const grid = readGrid();
		if (!grid) return;
		const client = { x: event.clientX, y: event.clientY };
		const origin = { x: client.x - grid.left, y: client.y - grid.top };
		if (origin.x < 0) return; // the hour gutter

		dragState.current = {
			pointerId: event.pointerId,
			origin,
			started: false,
			latestClient: client,
			latestRange: null,
		};
	}

	function handlePointerMove(event: ReactPointerEvent<HTMLDivElement>): void {
		const state = dragState.current;
		if (!state || event.pointerId !== state.pointerId) return;
		// The button was released somewhere this grid never heard about (before it had captured
		// the pointer): the press is over, so a later move must not resume it.
		if (event.buttons === 0) {
			if (state.started) cancelDrag();
			else dragState.current = null;
			return;
		}

		const wasStarted = state.started;
		updateDrag({ x: event.clientX, y: event.clientY });
		// Capture only once it is a drag, so a plain click or double-click keeps targeting the
		// column under the pointer exactly as it would without any of this.
		if (!wasStarted && state.started) {
			event.currentTarget.setPointerCapture(event.pointerId);
		}
	}

	function handlePointerUp(event: ReactPointerEvent<HTMLDivElement>): void {
		const state = dragState.current;
		if (!state || event.pointerId !== state.pointerId) return;

		updateDrag({ x: event.clientX, y: event.clientY });
		const range = state.latestRange;
		dragState.current = null;
		setDrag(null);
		if (!state.started || !range) return;

		const { start, end } = rangeDates(weekStart, range);
		onCreateRange(start, end);
	}

	function revealMinute(minute: number): void {
		const scroller = scrollerRef.current;
		if (!scroller) return;

		const { hourHeight: height, viewportHeight } = live.current;
		const top = (minute / 60) * height;
		const bottom = top + (step / 60) * height;
		if (top < scroller.scrollTop) scroller.scrollTop = top;
		else if (bottom > scroller.scrollTop + viewportHeight) {
			scroller.scrollTop = bottom - viewportHeight;
		}
	}

	function moveCursor(column: number, minute: number, extend: boolean): void {
		const nextColumn = clamp(column, 0, dayCount - 1);
		const nextMinute = clamp(minute, 0, minutesPerDay - step);
		setRangeAnchor(
			extend
				? (rangeAnchor ?? { column: cursor.column, minute: cursorMinute })
				: null,
		);
		setCursor({ column: nextColumn, minute: nextMinute });
		revealMinute(nextMinute);
		requestAnimationFrame(() => columnRefs.current[nextColumn]?.focus());
	}

	function keyboardRangeNow(): TimeRange | null {
		return rangeAnchor
			? keyboardRange(
					rangeAnchor,
					{ column: cursor.column, minute: cursorMinute },
					step,
				)
			: null;
	}

	function createFromCursor(): void {
		const range = keyboardRangeNow();
		if (range) {
			const { start, end } = rangeDates(weekStart, range);
			setRangeAnchor(null);
			onCreateRange(start, end);
			return;
		}
		onCreateAt(slotStart(days[cursor.column], cursorMinute, step));
	}

	/** Keyboard and button zoom anchor on the cursor when it is on screen, else the middle. */
	function zoomAboutCursor(factor: number): void {
		const scroller = scrollerRef.current;
		const { hourHeight: height, viewportHeight } = live.current;
		const offset =
			((cursorMinute + step / 2) / 60) * height - (scroller?.scrollTop ?? 0);
		zoomBy(
			factor,
			offset >= 0 && offset <= viewportHeight ? offset : viewportHeight / 2,
		);
	}

	function rangeLabel(range: TimeRange): string {
		const { start, end } = rangeDates(weekStart, range);
		return timeRangeLabel(
			start.toDate(),
			end.toDate(),
			calendarFormatters,
			true,
		);
	}

	const activeRange = drag?.range ?? keyboardRangeNow();
	const segments = activeRange ? rangeSegments(activeRange) : [];
	const rangeText = activeRange ? rangeLabel(activeRange) : null;

	const allDay = events
		.filter((event) => event.isAllDay)
		.flatMap((event) => {
			const span = allDayColumns(event, weekStart, dayCount);
			return span ? [{ event, span }] : [];
		});
	const allDayRows = packAllDayRows(allDay.map((item) => item.span));
	const strip = allDayStrip(allDayRows, allDayExpanded);

	const minimumMinutes = minimumBlockMinutesFor(hourHeight);
	const timed = events.filter((event) => !event.isAllDay);
	const columns = days.map((day) =>
		layoutLanes(
			timed.flatMap((event) => {
				const interval = timedBlockInterval(event, day, minimumMinutes);
				return interval ? [{ event, ...interval }] : [];
			}),
		),
	);

	const labelStep = labelStepHours(hourHeight);
	const zoomedOut = hourHeight <= fit;
	const zoomedIn = hourHeight >= Math.max(maxHourHeight, fit);

	return (
		<div className={styles.week}>
			<div className={styles.toolbar} role="toolbar" aria-label="Time scale">
				<IconButton
					label="Zoom out"
					kind="ghost"
					size="sm"
					align="bottom"
					disabled={zoomedOut}
					onClick={() => zoomAboutCursor(1 / zoomStepFactor)}
				>
					<ZoomOut />
				</IconButton>
				<IconButton
					label="Zoom in"
					kind="ghost"
					size="sm"
					align="bottom"
					disabled={zoomedIn}
					onClick={() => zoomAboutCursor(zoomStepFactor)}
				>
					<ZoomIn />
				</IconButton>
				<IconButton
					label="Fit 24 hours"
					kind="ghost"
					size="sm"
					align="bottom-end"
					disabled={zoomedOut}
					onClick={fitDay}
				>
					<ZoomFit />
				</IconButton>
			</div>
			<p className={styles.srOnly} role="status">
				{rangeText && !drag
					? `Selected ${rangeText}. Press Enter to add an event.`
					: ""}
			</p>

			<div
				ref={scrollerRef}
				className={styles.scroller}
				style={{ ["--mylomail-hour-height" as string]: `${hourHeight}px` }}
			>
				<div
					className={styles.grid}
					role="grid"
					aria-label={`Week of ${calendarFormatters.weekRange(
						weekStart.toDate(),
						weekStart.add(dayCount - 1, "day").toDate(),
					)}`}
				>
					<div ref={headRef} className={styles.head} role="rowgroup">
						<div className={styles.row} role="row">
							<div className={styles.gutterCell} role="presentation" />
							{days.map((day) => {
								const isToday = day.isSame(now, "day");
								return (
									<div
										key={day.toISOString()}
										className={styles.dayHeader}
										role="columnheader"
										aria-current={isToday ? "date" : undefined}
										aria-label={calendarFormatters.fullDate(day.toDate())}
										data-today={isToday}
									>
										<span className={styles.weekday} aria-hidden>
											{calendarFormatters.weekdayShort(day.toDate())}
										</span>
										<span className={styles.dayNumber} aria-hidden>
											{calendarFormatters.dayNumber(day.toDate())}
										</span>
									</div>
								);
							})}
						</div>
						<div className={styles.row} role="row">
							<div className={styles.gutterCell} role="rowheader">
								<span className={styles.allDayLabel}>All day</span>
							</div>
							<div
								className={styles.allDay}
								role="gridcell"
								aria-colspan={dayCount}
								aria-label="All-day events"
							>
								{allDay.map(({ event, span }, index) => {
									const row = allDayRows[index];
									if (row >= strip.visibleRows) return null;

									const place = locationText(event.location);
									const title = eventTitle(event.title);
									return (
										<button
											key={event.id}
											type="button"
											className={styles.allDayChip}
											style={{
												gridColumn: `${span.startColumn + 1} / ${span.endColumn + 1}`,
												gridRow: row + 1,
												["--mylomail-event-color" as string]: event.color,
												["--mylomail-event-text" as string]: readableTextColour(
													event.color,
												),
											}}
											data-conflict={event.syncConflict}
											title={place ? `${title}\n${place}` : title}
											aria-label={`${title}, all day${place ? `, ${place}` : ""}`}
											onClick={() => onSelectEvent(event.id)}
										>
											<span className={styles.chipTitle}>{title}</span>
											{place ? (
												<span
													className={styles.chipLocation}
												>{` · ${place}`}</span>
											) : null}
										</button>
									);
								})}
								{strip.overflowing ? (
									<button
										type="button"
										className={styles.allDayMore}
										style={{ gridRow: strip.visibleRows + 1 }}
										aria-expanded={allDayExpanded}
										onClick={() => setAllDayExpanded(!allDayExpanded)}
									>
										{allDayExpanded
											? "Show fewer all-day events"
											: `+${calendarFormatters.number(strip.hiddenEvents)} more all-day`}
									</button>
								) : null}
							</div>
						</div>
					</div>

					<div
						ref={bodyRef}
						className={styles.body}
						role="row"
						data-dragging={dragging}
						onPointerDown={handlePointerDown}
						onPointerMove={handlePointerMove}
						onPointerUp={handlePointerUp}
						onPointerCancel={cancelDrag}
						onLostPointerCapture={cancelDrag}
					>
						<div className={styles.gutter} role="presentation" aria-hidden>
							{hours
								.filter((hour) => hour > 0 && hour % labelStep === 0)
								.map((hour) => (
									<div
										key={hour}
										className={styles.hourLabel}
										style={{ top: hour * hourHeight }}
									>
										{calendarFormatters.hour(new Date(2000, 0, 1, hour))}
									</div>
								))}
						</div>
						{days.map((day, index) => {
							const isActive = index === cursor.column;
							const slotLabel = calendarFormatters.time(
								slotStart(day, cursorMinute, step).toDate(),
							);
							const fullDate = calendarFormatters.fullDate(day.toDate());
							return (
								// A pointer shortcut for the same action as Enter: the column is the
								// tab stop (one per week, the active day), the cursor inside it is
								// drawn from `cursor.minute`.
								<div
									key={day.toISOString()}
									ref={(element) => {
										columnRefs.current[index] = element;
									}}
									className={styles.column}
									role="gridcell"
									tabIndex={isActive ? 0 : -1}
									aria-label={
										isActive
											? `${fullDate}, ${slotLabel}. Press Enter to add an event, Shift and arrow keys to select a range.`
											: fullDate
									}
									data-today={day.isSame(now, "day")}
									data-minor={minorGridlines(hourHeight)}
									onFocus={(event) => {
										if (event.target === event.currentTarget && !isActive) {
											setCursor((current) => ({ ...current, column: index }));
										}
									}}
									onKeyDown={(event) => {
										// Keys on an event block inside the column belong to the block.
										if (event.target !== event.currentTarget) return;
										if (event.ctrlKey || event.metaKey || event.altKey) return;

										const extend = event.shiftKey;
										switch (event.key) {
											case "ArrowLeft":
												moveCursor(index - 1, cursorMinute, extend);
												break;
											case "ArrowRight":
												moveCursor(index + 1, cursorMinute, extend);
												break;
											case "Home":
												moveCursor(0, cursorMinute, extend);
												break;
											case "End":
												moveCursor(dayCount - 1, cursorMinute, extend);
												break;
											case "ArrowUp":
												moveCursor(index, cursorMinute - step, extend);
												break;
											case "ArrowDown":
												moveCursor(index, cursorMinute + step, extend);
												break;
											case "Enter":
											case " ":
												createFromCursor();
												break;
											case "Escape":
												if (!rangeAnchor) return;
												setRangeAnchor(null);
												break;
											case "+":
											case "=":
												zoomAboutCursor(zoomStepFactor);
												break;
											case "-":
											case "_":
												zoomAboutCursor(1 / zoomStepFactor);
												break;
											case "0":
												fitDay();
												break;
											default:
												return;
										}
										event.preventDefault();
									}}
									onDoubleClick={(event) => {
										// Empty space only: an event block opens its event instead.
										if ((event.target as HTMLElement).closest("button")) return;

										const bounds = event.currentTarget.getBoundingClientRect();
										const minute =
											((event.clientY - bounds.top) / bounds.height) *
											minutesPerDay;
										onCreateAt(slotStart(day, minute, step));
									}}
								>
									{columns[index].map(
										({ event, startMinute, endMinute, lane, laneCount }) => {
											const place = locationText(event.location);
											const title = eventTitle(event.title);
											const when = timeRangeLabel(
												new Date(event.start),
												new Date(event.end),
												calendarFormatters,
											);
											const content = blockContent(
												((endMinute - startMinute) / 60) * hourHeight,
												columnWidth / laneCount,
												place !== null,
											);
											return (
												<button
													key={event.id}
													type="button"
													className={styles.block}
													style={{
														top: percentOfDay(startMinute),
														height: percentOfDay(endMinute - startMinute),
														left: `${(lane / laneCount) * 100}%`,
														width: `calc(${100 / laneCount}% - 2px)`,
														["--mylomail-event-color" as string]: event.color,
														["--mylomail-event-text" as string]:
															readableTextColour(event.color),
													}}
													data-conflict={event.syncConflict}
													title={[title, when, place]
														.filter(Boolean)
														.join("\n")}
													aria-label={[
														title,
														when,
														place,
														calendarFormatters.monthDay(day.toDate()),
													]
														.filter(Boolean)
														.join(", ")}
													onClick={() => onSelectEvent(event.id)}
												>
													<span className={styles.blockTitle}>{title}</span>
													{content.time ? (
														<span className={styles.blockTime}>{when}</span>
													) : null}
													{content.location ? (
														<span className={styles.blockLocation}>
															{place}
														</span>
													) : null}
												</button>
											);
										},
									)}
									{day.isSame(now, "day") ? (
										<div
											className={styles.nowLine}
											style={{
												top: percentOfDay(now.hour() * 60 + now.minute()),
											}}
											aria-hidden
										/>
									) : null}
									{segments
										.filter((segment) => segment.column === index)
										.map((segment) => (
											<div
												key={segment.startMinute}
												className={styles.selection}
												style={{
													top: percentOfDay(segment.startMinute),
													height: percentOfDay(
														segment.endMinute - segment.startMinute,
													),
												}}
												aria-hidden
											/>
										))}
									{isActive ? (
										<div
											className={styles.cursor}
											style={{
												top: percentOfDay(cursorMinute),
												height: percentOfDay(step),
											}}
											aria-hidden
										/>
									) : null}
								</div>
							);
						})}
					</div>
				</div>
			</div>
			{drag && rangeText ? (
				<div
					className={styles.dragLabel}
					style={{ left: drag.client.x + 14, top: drag.client.y + 18 }}
					aria-hidden
				>
					{rangeText}
				</div>
			) : null}
		</div>
	);
}
