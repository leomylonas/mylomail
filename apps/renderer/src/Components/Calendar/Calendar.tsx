import { useMemo, useState } from "react";
import { useMutation, useQueries, useQueryClient } from "@tanstack/react-query";
import type { HubConnection } from "@microsoft/signalr";
import { Button, ContentSwitcher, Switch } from "@carbon/react";
import { queryKeys } from "@mylomail/renderer/Shell/Backend/HubConnection";
import { useWindowNotifications } from "@mylomail/renderer/Shell/Registries/Notifications/UseNotifications";
import { notify } from "@mylomail/renderer/Shell/Registries/Notifications/NotificationStore";
import { CalendarGrid } from "@mylomail/renderer/Components/Calendar/CalendarGrid/CalendarGrid";
import { CalendarAgenda } from "@mylomail/renderer/Components/Calendar/CalendarAgenda/CalendarAgenda";
import {
	EventModal,
	type EventFormValues,
} from "@mylomail/renderer/Components/Calendar/EventModal/EventModal";
import { dayjs, type Dayjs } from "@mylomail/renderer/Lib/DayjsSetup";
import styles from "@mylomail/renderer/Components/Calendar/Calendar.module.css";

interface CalendarSummary {
	id: string;
	accountId: string;
	name: string;
	colour: string | null;
	isDefault: boolean;
}

interface CalendarEventSummary {
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
}

type ModalState =
	| { mode: "create"; calendarId: string; date: Dayjs }
	| { mode: "edit"; event: CalendarEventSummary }
	| null;

/**
 * Unified across every account's calendars, colour-distinguished by account (§13 Epic 7).
 * Each account already has a colour the sidebar uses; a calendar's own colour, when the
 * provider supplies one, wins over it.
 */
export function Calendar({
	hub,
	accounts,
}: {
	hub: HubConnection;
	accounts: { id: string; color: string }[];
}) {
	const [anchor, setAnchor] = useState(() => dayjs());
	const [view, setView] = useState<"grid" | "agenda">("grid");
	const [modal, setModal] = useState<ModalState>(null);
	const queryClient = useQueryClient();
	const { store: notifications } = useWindowNotifications();

	const calendarQueries = useQueries({
		queries: accounts.map((account) => ({
			queryKey: queryKeys.calendars(account.id),
			queryFn: () => hub.invoke<CalendarSummary[]>("GetCalendars", account.id),
		})),
	});

	const calendars = useMemo(
		() =>
			calendarQueries.flatMap((query, index) =>
				(query.data ?? []).map((calendar) => ({
					...calendar,
					accountColor: accounts[index].color,
				})),
			),
		[calendarQueries, accounts],
	);

	// Six full weeks so the grid never reflows, and the same window doubles as the agenda's
	// range — one set of navigation controls for both views.
	const rangeStart = anchor.startOf("month").startOf("week");
	const rangeEnd = anchor.endOf("month").endOf("week");

	const eventQueries = useQueries({
		queries: calendars.map((calendar) => ({
			queryKey: queryKeys.calendarEvents(
				calendar.id,
				rangeStart.toISOString(),
				rangeEnd.toISOString(),
			),
			queryFn: () =>
				hub.invoke<CalendarEventSummary[]>(
					"GetCalendarEvents",
					calendar.id,
					rangeStart.toISOString(),
					rangeEnd.toISOString(),
				),
			enabled: calendars.length > 0,
		})),
	});

	const events = useMemo(
		() =>
			eventQueries.flatMap((query, index) => {
				const calendar = calendars[index];
				return (query.data ?? []).map((event) => ({
					...event,
					color: calendar.colour ?? calendar.accountColor,
				}));
			}),
		[eventQueries, calendars],
	);

	const eventsById = useMemo(
		() => new Map(events.map((event) => [event.id, event])),
		[events],
	);

	const invalidate = () =>
		queryClient.invalidateQueries({ queryKey: ["calendar-events"] });

	const save = useMutation({
		mutationFn: (values: EventFormValues) =>
			hub.invoke("SaveCalendarEvent", {
				eventId: values.eventId,
				calendarId: values.calendarId,
				title: values.title,
				location: values.location || undefined,
				description: values.description || undefined,
				start: values.start,
				end: values.end,
				isAllDay: values.isAllDay,
			}),
		onSuccess: () => {
			setModal(null);
			void invalidate();
		},
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The event could not be saved",
				detail: error instanceof Error ? error.message : String(error),
			}),
	});

	const remove = useMutation({
		mutationFn: (eventId: string) => hub.invoke("DeleteCalendarEvent", eventId),
		onSuccess: () => {
			setModal(null);
			void invalidate();
		},
		onError: (error: unknown) =>
			notify(notifications, {
				kind: "error",
				title: "The event could not be deleted",
				detail: error instanceof Error ? error.message : String(error),
			}),
	});

	const defaultCalendarId =
		calendars.find((c) => c.isDefault)?.id ?? calendars[0]?.id;
	const calendarsSettled = calendarQueries.every((query) => !query.isPending);
	const noCalendars = calendarsSettled && calendars.length === 0;

	return (
		<div className={styles.calendar}>
			<header className={styles.header}>
				<div className={styles.nav}>
					<Button
						kind="ghost"
						size="sm"
						onClick={() => setAnchor(anchor.subtract(1, "month"))}
					>
						‹
					</Button>
					<Button kind="ghost" size="sm" onClick={() => setAnchor(dayjs())}>
						Today
					</Button>
					<Button
						kind="ghost"
						size="sm"
						onClick={() => setAnchor(anchor.add(1, "month"))}
					>
						›
					</Button>
					<h2 className={styles.month}>{anchor.format("MMMM YYYY")}</h2>
				</div>
				<div className={styles.switcher}>
					<ContentSwitcher
						size="sm"
						selectedIndex={view === "grid" ? 0 : 1}
						onChange={({ index }) => setView(index === 0 ? "grid" : "agenda")}
					>
						<Switch name="grid" text="Month" />
						<Switch name="agenda" text="Agenda" />
					</ContentSwitcher>
				</div>
			</header>

			<div className={styles.body}>
				{noCalendars ? (
					<p className={styles.empty}>
						No calendars are configured on any account yet.
					</p>
				) : view === "grid" ? (
					<CalendarGrid
						anchor={anchor}
						events={events}
						onSelectDay={(date) => {
							if (defaultCalendarId) {
								setModal({
									mode: "create",
									calendarId: defaultCalendarId,
									date,
								});
							}
						}}
						onSelectEvent={(eventId) => {
							const event = eventsById.get(eventId);
							if (event) setModal({ mode: "edit", event });
						}}
					/>
				) : (
					<CalendarAgenda
						rangeStart={rangeStart}
						rangeEnd={rangeEnd}
						events={events}
						onSelectEvent={(eventId) => {
							const event = eventsById.get(eventId);
							if (event) setModal({ mode: "edit", event });
						}}
					/>
				)}
			</div>

			{modal ? (
				<EventModal
					hub={hub}
					initial={toFormValues(modal)}
					syncConflict={
						modal.mode === "edit" ? modal.event.syncConflict : false
					}
					onSave={(values) => save.mutate(values)}
					onDelete={
						modal.mode === "edit"
							? () => remove.mutate(modal.event.id)
							: undefined
					}
					onClose={() => setModal(null)}
				/>
			) : null}
		</div>
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
		};
	}

	const start = modal.date.hour(9).minute(0).second(0);
	return {
		calendarId: modal.calendarId,
		title: "",
		location: "",
		description: "",
		start: start.toISOString(),
		end: start.add(1, "hour").toISOString(),
		isAllDay: false,
	};
}
