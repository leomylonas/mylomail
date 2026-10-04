import {
	useCallback,
	useEffect,
	useMemo,
	useState,
	type ReactNode,
} from "react";
import { Group, Panel, Separator } from "react-resizable-panels";
import type { MailHubConnection } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useMutation, useQueries, useQueryClient } from "@tanstack/react-query";
import { ProviderType } from "@mylomail/shared-types/SignalR/MyloMail.Api.Domain";
import type { CalendarEventSummaryDto } from "@mylomail/shared-types/SignalR/MyloMail.Api.Contracts";
import { Button, ContentSwitcher, Switch } from "@carbon/react";
import { Add } from "@carbon/icons-react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { notificationForError } from "@mylomail/renderer/Shell/Backend/ProblemDetailsTransport";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import {
	useShellLayout,
	withSidebarWidth,
} from "@mylomail/renderer/Shell/Layout/UseShellLayout";
import { useWindowStore } from "@mylomail/renderer/Shell/WindowScope/WindowScope";
import { useStoreValue } from "@mylomail/renderer/Shell/WindowScope/UseStoreValue";
import { CalendarGrid } from "@mylomail/renderer/Components/Calendar/CalendarGrid/CalendarGrid";
import { CalendarWeek } from "@mylomail/renderer/Components/Calendar/CalendarWeek/CalendarWeek";
import { CalendarAgenda } from "@mylomail/renderer/Components/Calendar/CalendarAgenda/CalendarAgenda";
import {
	CalendarSidebar,
	type CalendarSidebarAccount,
} from "@mylomail/renderer/Components/Calendar/CalendarSidebar/CalendarSidebar";
import {
	EventModal,
	type EventFormValues,
} from "@mylomail/renderer/Components/Calendar/EventModal/EventModal";
import {
	defaultCalendarTimeZone,
	newEventRange,
	parseRecurrenceDateLines,
	recurrenceRuleLines,
} from "@mylomail/renderer/Components/Calendar/EventModal/EventScheduling";
import {
	readableTextColour,
	resolveCalendarColour,
} from "@mylomail/renderer/Components/Calendar/CalendarColour";
import {
	calendarsInView,
	isCalendarHidden,
	seedHiddenState,
} from "@mylomail/renderer/Components/Calendar/CalendarVisibility";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import {
	calendarFormatters,
	calendarGridStart,
	calendarWeekStart,
} from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import styles from "@mylomail/renderer/Components/Calendar/Calendar.module.css";

const sidebarPanelId = "calendar-sidebar";
const mainPanelId = "calendar-main";

const views = ["grid", "week", "agenda"] as const;
type CalendarView = (typeof views)[number];

export interface CalendarEventSummary {
	id: string;
	calendarId: string;
	title: string;
	location: string | null;
	description: string | null;
	start: string;
	end: string;
	isAllDay: boolean;
	isRecurring: boolean;
	syncConflict: boolean;
	/**
	 * A generated occurrence of a recurring series, not a real row (§13 Epic 7) — `id` is a
	 * stable derived id, not something `GetCalendarEventDetail`/`SaveCalendarEvent` can look
	 * up. `masterEventId` is the real row to edit instead: today's editing only reaches the
	 * whole series, not this one not-yet-overridden occurrence (per-occurrence editing exists
	 * only for an override CalDAV sync already materialised as its own row).
	 */
	isVirtualOccurrence: boolean;
	masterEventId: string | null;
	/**
	 * True only for the row that owns the series' recurrence rules (or a virtual occurrence
	 * generated from one) — false for an already-materialised override/exception row, even
	 * though that row's own `isRecurring` is also true. Deleting a master or virtual occurrence
	 * removes the whole series; deleting an override removes only that one occurrence.
	 */
	isRecurrenceMaster: boolean;
}

function calendarEventSummary(
	event: CalendarEventSummaryDto,
): CalendarEventSummary {
	return {
		id: event.id,
		calendarId: event.calendarId,
		title: event.title,
		location: event.location ?? null,
		description: event.description ?? null,
		start:
			typeof event.start === "string" ? event.start : event.start.toISOString(),
		end: typeof event.end === "string" ? event.end : event.end.toISOString(),
		isAllDay: event.isAllDay,
		isRecurring: event.isRecurring,
		syncConflict: event.syncConflict,
		isVirtualOccurrence: event.isVirtualOccurrence,
		masterEventId: event.masterEventId ?? null,
		isRecurrenceMaster: event.isRecurrenceMaster,
	};
}

export type ModalState =
	| {
			mode: "create";
			calendarId: string;
			/** The start: a day (opened at 09:00) or, with `atTime`, an exact moment. */
			date: Dayjs;
			atTime?: boolean;
			/** An explicit end, from a dragged range; absent means one hour after the start. */
			end?: Dayjs;
	  }
	| { mode: "edit"; event: CalendarEventSummary }
	| null;

/**
 * `modal.event` is a snapshot frozen at the moment the modal opened — correct for the values
 * that seed `EventModal`'s own once-only editing state, but wrong for anything that should
 * reflect what's happening right now: `syncConflict`, `isRecurrenceMaster`,
 * `isVirtualOccurrence`. A background sync landing while the modal stays open (an organiser
 * update, a conflicting remote edit) refetches `eventsById` via the existing
 * `CalendarEventUpdated`/`CalendarConflictDetected` invalidation, but reading only the frozen
 * snapshot would mean the modal's own props never reflect it — the same "open session doesn't
 * learn about a relevant background change" shape already fixed for Compose/ReadingPane. Falls
 * back to the frozen snapshot if the event has dropped out of the current range/query window
 * (e.g. its own edit rescheduled it outside the visible month) rather than resolving to nothing
 * mid-edit.
 */
export function resolveLiveModalEvent(
	modal: ModalState,
	eventsById: Map<string, CalendarEventSummary>,
): CalendarEventSummary | undefined {
	if (modal?.mode !== "edit") return undefined;

	const direct = eventsById.get(modal.event.id);
	if (direct) return direct;

	for (const event of eventsById.values()) {
		if (event.masterEventId === modal.event.id) return event;
	}

	return modal.event;
}

/**
 * The month grid and agenda across the calendars in view (§13 Epic 7). Every account's
 * calendars are listed in the left panel, each with a checkbox for whether it joins the unified
 * view, and each is drawn in its own colour (see `CalendarColour`). With `onlyCalendarId` the
 * view is one calendar in a window of its own: no left panel, and visibility does not apply.
 */
export function Calendar({
	hub,
	accounts,
	onlyCalendarId,
}: {
	hub: MailHubConnection;
	accounts: {
		id: string;
		displayName: string;
		emailAddress?: string;
		color: string;
		providerType?: ProviderType;
	}[];
	onlyCalendarId?: string;
}) {
	const [anchor, setAnchor] = useState(() => dayjs());
	const [view, setView] = useState<CalendarView>("grid");
	const [takeAgendaFocus, setTakeAgendaFocus] = useState(false);
	const clearAgendaFocusRequest = useCallback(
		() => setTakeAgendaFocus(false),
		[],
	);
	const [modal, setModal] = useState<ModalState>(null);
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();
	const store = useWindowStore();
	const hiddenLive = useStoreValue(store, "calendarHidden");

	const calendarQueries = useQueries({
		queries: accounts.map((account) => ({
			queryKey: queryKeys.calendars(account.id),
			queryFn: () => hub.getCalendars(account.id),
		})),
	});

	const calendars = useMemo(
		() =>
			calendarQueries.flatMap((query, index) =>
				[...(query.data ?? [])]
					.sort(
						(a, b) =>
							Number(b.isDefault) - Number(a.isDefault) ||
							a.name.localeCompare(b.name),
					)
					.map((calendar) => ({
						...calendar,
						displayColour: resolveCalendarColour(calendar.colour, calendar.id),
						accountProviderType:
							accounts[index].providerType ?? ProviderType.Imap,
					})),
			),
		[calendarQueries, accounts],
	);

	// The server's persisted default is copied into this window's store once per calendar;
	// from then on the window's own value wins (see CalendarVisibility).
	useEffect(() => {
		const current = store.getState("calendarHidden");
		const seeded = seedHiddenState(current, calendars);
		if (seeded !== current) store.setState("calendarHidden", seeded);
	}, [calendars, store]);

	const inView = useMemo(
		() => calendarsInView(calendars, hiddenLive, onlyCalendarId),
		[calendars, hiddenLive, onlyCalendarId],
	);

	// Six full weeks so the grid never reflows, and the same window doubles as the agenda's
	// range — one set of navigation controls for both views.
	// Memoized rather than computed inline: Dayjs.startOf()/endOf() return a fresh instance
	// every call even for the same underlying date, and CalendarAgenda's roving-focus reset
	// effect depends on this object's identity — an unmemoized value would reset keyboard focus
	// on every Calendar re-render (a click opening EventModal, an unrelated sync refresh), not
	// just a genuine month/anchor change.
	const rangeStart = useMemo(() => calendarGridStart(anchor), [anchor]);
	const rangeEnd = useMemo(
		() => calendarGridStart(anchor).add(41, "day").endOf("day"),
		[anchor],
	);
	const weekStart = useMemo(() => calendarWeekStart(anchor), [anchor]);

	const eventQueries = useQueries({
		queries: inView.map((calendar) => ({
			queryKey: queryKeys.calendarEvents(
				calendar.id,
				rangeStart.toISOString(),
				rangeEnd.toISOString(),
			),
			queryFn: () =>
				hub.getCalendarEvents(
					calendar.id,
					rangeStart.toISOString(),
					rangeEnd.toISOString(),
				),
		})),
	});

	// Only the calendars in view are queried, so a hidden calendar's events never reach the
	// grid or the agenda; a calendar ticked back on is served from the query cache.
	const events = useMemo(
		() =>
			eventQueries.flatMap((query, index) => {
				const calendar = inView[index];
				return (query.data ?? []).map((event) => ({
					...calendarEventSummary(event),
					color: calendar.displayColour,
				}));
			}),
		[eventQueries, inView],
	);

	const eventsById = useMemo(
		() => new Map(events.map((event) => [event.id, event])),
		[events],
	);

	const invalidate = () =>
		queryClient.invalidateQueries({ queryKey: ["calendar-events"] });

	const liveModalEvent = resolveLiveModalEvent(modal, eventsById);
	const modalCalendarId =
		modal?.mode === "create" ? modal.calendarId : modal?.event.calendarId;
	const modalProviderType = calendars.find(
		(calendar) => calendar.id === modalCalendarId,
	)?.accountProviderType;

	// A virtual occurrence's own id is a derived id, not a real row — GetCalendarEventDetail,
	// DeleteCalendarEvent and ResolveEventConflict all address a real EventId, so opening one
	// substitutes masterEventId in its place. Editing therefore reaches the whole series, not
	// this one not-yet-overridden occurrence, until per-occurrence editing of a virtual
	// occurrence is built (§13 Epic 7) — the master is never itself present in the fetched
	// list to look up (every one of its own occurrences, including the first, is expanded),
	// so this substitutes the id directly rather than trying to find a summary row for it.
	const openEditModal = (eventId: string) => {
		const event = eventsById.get(eventId);
		if (!event) return;
		const target =
			event.isVirtualOccurrence && event.masterEventId
				? { ...event, id: event.masterEventId }
				: event;
		setModal({ mode: "edit", event: target });
	};

	const save = useMutation({
		mutationFn: (values: EventFormValues) =>
			hub.saveCalendarEvent({
				eventId: values.eventId,
				calendarId: values.calendarId,
				title: values.title,
				location: values.location || undefined,
				description: values.description || undefined,
				start: values.start,
				end: values.end,
				isAllDay: values.isAllDay,
				startTimeZoneId: values.isAllDay ? undefined : values.startTimeZoneId,
				endTimeZoneId: values.isAllDay ? undefined : values.endTimeZoneId,
				recurrenceRules: recurrenceRuleLines(values.recurrenceRulesText),
				recurrenceDates: parseRecurrenceDateLines(
					values.recurrenceDatesText,
					values.startTimeZoneId,
					values.isAllDay,
				),
				exceptionDates: parseRecurrenceDateLines(
					values.exceptionDatesText,
					values.startTimeZoneId,
					values.isAllDay,
				),
			}),
		onSuccess: () => {
			setModal(null);
			void invalidate();
		},
		onError: (error: unknown) =>
			notify(
				notifications,
				notificationForError(error, "The event could not be saved"),
			),
	});

	const remove = useMutation({
		mutationFn: (eventId: string) => hub.deleteCalendarEvent(eventId),
		onSuccess: () => {
			setModal(null);
			void invalidate();
		},
		onError: (error: unknown) =>
			notify(
				notifications,
				notificationForError(error, "The event could not be deleted"),
			),
	});

	// "Keep mine" / "keep theirs" for a flagged conflict (§15). Either way the conflict is
	// settled server-side, so the modal closes the same as a normal save — there is nothing
	// left in it for the user to decide once this resolves.
	const resolveConflict = useMutation({
		mutationFn: ({
			eventId,
			keepMine,
		}: {
			eventId: string;
			keepMine: boolean;
		}) => hub.resolveEventConflict(eventId, keepMine),
		onSuccess: () => {
			setModal(null);
			void invalidate();
		},
		onError: (error: unknown) =>
			notify(
				notifications,
				notificationForError(error, "The conflict could not be resolved"),
			),
	});

	const newEventCalendar =
		inView.find((calendar) => calendar.isDefault) ?? inView[0];
	const calendarsSettled = calendarQueries.every((query) => !query.isPending);
	const noCalendars = calendarsSettled && calendars.length === 0;
	const singleCalendar =
		onlyCalendarId === undefined
			? undefined
			: calendars.find((calendar) => calendar.id === onlyCalendarId);
	const unavailable =
		onlyCalendarId !== undefined && calendarsSettled && !singleCalendar;
	const allHidden =
		onlyCalendarId === undefined && calendars.length > 0 && inView.length === 0;

	// The month label, and Previous/Next, follow the view: a week at a time in the week view,
	// otherwise a month. The agenda opens on the anchor day, so choosing a day number in the
	// month grid makes that day the anchor and the agenda's first row.
	const step = view === "week" ? "week" : "month";
	const title =
		view === "week"
			? calendarFormatters.weekRange(
					weekStart.toDate(),
					weekStart.add(6, "day").toDate(),
				)
			: calendarFormatters.monthYear(anchor.toDate());

	const openCreate = (
		date: Dayjs,
		options: { atTime?: boolean; end?: Dayjs } = {},
	) => {
		if (!newEventCalendar) return;
		setModal({
			mode: "create",
			calendarId: newEventCalendar.id,
			date,
			...options,
		});
	};

	const focusDayInAgenda = (date: Dayjs) => {
		setAnchor(date);
		setView("agenda");
		setTakeAgendaFocus(true);
	};

	// A tick takes effect in this window at once and is saved as the default the next window
	// opens with. The save is not announced to other windows, so none of them moves.
	const setCalendarHidden = useMutation({
		mutationFn: (input: { calendarId: string; hidden: boolean }) =>
			hub.setCalendarHidden(input.calendarId, input.hidden),
		onError: (error: unknown, input) => {
			store.setState("calendarHidden", {
				...store.getState("calendarHidden"),
				[input.calendarId]: !input.hidden,
			});
			notify(
				notifications,
				notificationForError(error, "The calendar setting could not be saved"),
			);
		},
	});

	const toggleCalendar = (calendarId: string, visible: boolean) => {
		store.setState("calendarHidden", {
			...store.getState("calendarHidden"),
			[calendarId]: !visible,
		});
		setCalendarHidden.mutate({ calendarId, hidden: !visible });
	};

	const openCalendarWindow = (calendarId: string, accountId: string) => {
		void window.windows
			?.open(
				new URLSearchParams({
					calendar: calendarId,
					account: accountId,
				}).toString(),
			)
			.catch(() => {
				notify(notifications, {
					kind: "error",
					title: "Could not open in a new window",
					detail: "The calendar is still available here instead.",
				});
			});
	};

	const sidebarAccounts: CalendarSidebarAccount[] = accounts.map(
		(account, index) => ({
			id: account.id,
			displayName: account.displayName,
			emailAddress: account.emailAddress,
			color: account.color,
			loaded: !calendarQueries[index].isPending,
			calendars: calendars
				.filter((calendar) => calendar.accountId === account.id)
				.map((calendar) => ({
					id: calendar.id,
					name: calendar.name,
					colour: calendar.displayColour,
					visible: !isCalendarHidden(calendar, hiddenLive),
				})),
		}),
	);

	const message = unavailable
		? "This calendar is no longer available."
		: noCalendars
			? "No calendars are configured on any account yet."
			: allHidden
				? "Every calendar is hidden. Tick a calendar in the list to show it."
				: null;

	const main = (
		<div className={styles.main}>
			<header className={styles.header}>
				<div className={styles.nav}>
					{singleCalendar ? (
						<h1
							className={styles.calendarName}
							style={{
								backgroundColor: singleCalendar.displayColour,
								color: readableTextColour(singleCalendar.displayColour),
							}}
						>
							{singleCalendar.name || "Untitled calendar"}
						</h1>
					) : null}
					<Button
						kind="ghost"
						size="sm"
						aria-label={`Previous ${step}`}
						onClick={() => setAnchor(anchor.subtract(1, step))}
					>
						‹
					</Button>
					<Button kind="ghost" size="sm" onClick={() => setAnchor(dayjs())}>
						Today
					</Button>
					<Button
						kind="ghost"
						size="sm"
						aria-label={`Next ${step}`}
						onClick={() => setAnchor(anchor.add(1, step))}
					>
						›
					</Button>
					<h2 className={styles.month}>{title}</h2>
				</div>
				<div className={styles.actions}>
					<Button
						kind="primary"
						size="sm"
						renderIcon={Add}
						disabled={!newEventCalendar}
						onClick={() => openCreate(anchor)}
					>
						New event
					</Button>
					<div className={styles.switcher}>
						<ContentSwitcher
							size="sm"
							selectedIndex={views.indexOf(view)}
							onChange={({ name }) => {
								const next = views.find((candidate) => candidate === name);
								if (next) setView(next);
							}}
						>
							<Switch name="grid" text="Month" />
							<Switch name="week" text="Week" />
							<Switch name="agenda" text="Agenda" />
						</ContentSwitcher>
					</div>
				</div>
			</header>

			<div className={styles.body}>
				{message ? (
					<p className={styles.empty}>{message}</p>
				) : view === "grid" ? (
					<CalendarGrid
						anchor={anchor}
						events={events}
						onFocusDay={focusDayInAgenda}
						onCreateOnDay={(date) => openCreate(date)}
						onSelectEvent={openEditModal}
					/>
				) : view === "week" ? (
					<CalendarWeek
						weekStart={weekStart}
						events={events}
						onSelectEvent={openEditModal}
						onCreateAt={(start) => openCreate(start, { atTime: true })}
						onCreateRange={(start, end) =>
							openCreate(start, { atTime: true, end })
						}
					/>
				) : (
					<CalendarAgenda
						rangeStart={rangeStart}
						rangeEnd={rangeEnd}
						focusDate={anchor}
						takeFocus={takeAgendaFocus}
						onFocusTaken={clearAgendaFocusRequest}
						events={events}
						onSelectEvent={openEditModal}
					/>
				)}
			</div>
		</div>
	);

	return (
		<div className={styles.calendar}>
			{onlyCalendarId === undefined ? (
				<CalendarWithSidebar
					sidebar={
						<CalendarSidebar
							accounts={sidebarAccounts}
							onToggleCalendar={toggleCalendar}
							onOpenCalendar={openCalendarWindow}
						/>
					}
				>
					{main}
				</CalendarWithSidebar>
			) : (
				main
			)}

			{modal ? (
				<EventModal
					hub={hub}
					initial={toFormValues(modal)}
					syncConflict={
						modal.mode === "edit" ? liveModalEvent?.syncConflict : false
					}
					deletesWholeSeries={
						modal.mode === "edit" ? liveModalEvent?.isRecurrenceMaster : false
					}
					recurrenceEditable={
						modal.mode !== "edit" ||
						!liveModalEvent?.isRecurring ||
						Boolean(liveModalEvent.isRecurrenceMaster)
					}
					supportsRecurrenceSets={
						modalProviderType !== ProviderType.Microsoft365
					}
					onSave={(values) => save.mutate(values)}
					onDelete={
						modal.mode === "edit"
							? () => remove.mutate(modal.event.id)
							: undefined
					}
					onResolveConflict={
						modal.mode === "edit"
							? (keepMine) =>
									resolveConflict.mutate({
										eventId: modal.event.id,
										keepMine,
									})
							: undefined
					}
					onClose={() => setModal(null)}
				/>
			) : null}
		</div>
	);
}

/**
 * The left panel beside the calendar, resizable and sized like the mail view's folder pane:
 * the width is the shell's shared default (`useShellLayout`, read once when the window opens),
 * a drag writes it back for future windows and leaves windows already open alone.
 */
function CalendarWithSidebar({
	sidebar,
	children,
}: {
	sidebar: ReactNode;
	children: ReactNode;
}) {
	const { store: notifications } = useWindowNotifications();
	const layout = useShellLayout((error) =>
		notify(
			notifications,
			notificationForError(error, "The panel layout could not be saved"),
		),
	);

	if (!layout.ready) return <div className={styles.panels} />;

	return (
		<Group
			className={styles.panels}
			defaultLayout={{
				[sidebarPanelId]: layout.initial.sidebar,
				[mainPanelId]: 100 - layout.initial.sidebar,
			}}
			onLayoutChanged={(sizes, meta) => {
				// Only a direct drag is the user's stated preference, as in the mail view.
				if (!meta.isUserInteraction) return;
				layout.onResize(
					withSidebarWidth(layout.initial, sizes[sidebarPanelId]),
				);
			}}
		>
			<Panel id={sidebarPanelId} minSize="140px">
				{sidebar}
			</Panel>
			<Separator className={styles.handle} />
			<Panel id={mainPanelId} minSize="30">
				{children}
			</Panel>
		</Group>
	);
}

function toFormValues(modal: NonNullable<ModalState>): EventFormValues {
	if (modal.mode === "edit") {
		return {
			eventId: modal.event.id,
			calendarId: modal.event.calendarId,
			title: modal.event.title,
			location: modal.event.location ?? "",
			description: modal.event.description ?? "",
			start: modal.event.start,
			end: modal.event.end,
			isAllDay: modal.event.isAllDay,
			startTimeZoneId: defaultCalendarTimeZone,
			endTimeZoneId: defaultCalendarTimeZone,
			recurrenceRulesText: "",
			recurrenceDatesText: "",
			exceptionDatesText: "",
		};
	}

	const { start, end } = newEventRange(modal.date, {
		atTime: modal.atTime,
		end: modal.end,
	});
	return {
		calendarId: modal.calendarId,
		title: "",
		location: "",
		description: "",
		start: start.toISOString(),
		end: end.toISOString(),
		isAllDay: false,
		startTimeZoneId: defaultCalendarTimeZone,
		endTimeZoneId: defaultCalendarTimeZone,
		recurrenceRulesText: "",
		recurrenceDatesText: "",
		exceptionDatesText: "",
	};
}
