import { useCalendarFormatters } from "@mylomail/renderer/Hooks/UseCalendarFormatters";
import dayjs from "dayjs";
import styles from "@mylomail/renderer/Components/Calendar/CalendarSearchResults/CalendarSearchResults.module.css";

export interface CalendarSearchResult {
	id: string;
	title: string;
	location: string | null;
	start: string;
	end: string;
	isAllDay: boolean;
	isRecurring: boolean;
	color: string;
	/** "Account · Calendar", so the same title in two calendars is told apart. */
	where: string;
}

/** Events matching the search bar, across all time and every calendar in view, soonest first. */
export function CalendarSearchResults({
	results,
	loading,
	query,
	onSelect,
}: {
	results: readonly CalendarSearchResult[];
	loading: boolean;
	query: string;
	onSelect: (eventId: string) => void;
}) {
	const formatters = useCalendarFormatters();
	if (loading && results.length === 0)
		return (
			<p className={styles.empty} role="status">
				Searching…
			</p>
		);
	if (results.length === 0)
		return (
			<p className={styles.empty} role="status">
				No events match “{query.trim()}”.
			</p>
		);
	return (
		<ul className={styles.results} aria-label="Matching events">
			{results.map((event) => {
				const start = dayjs(event.start);
				return (
					<li key={event.id}>
						<button
							type="button"
							className={styles.result}
							onClick={() => onSelect(event.id)}
						>
							<span
								className={styles.swatch}
								style={{ backgroundColor: event.color }}
								aria-hidden="true"
							/>
							<span className={styles.when}>
								{formatters.fullDate(start.toDate())}
								{event.isAllDay ? "" : `, ${formatters.time(start.toDate())}`}
								{event.isRecurring ? " · repeats" : ""}
							</span>
							<span className={styles.title}>
								{event.title || "(No title)"}
							</span>
							<span className={styles.where}>
								{[event.where, event.location].filter(Boolean).join(" · ")}
							</span>
						</button>
					</li>
				);
			})}
		</ul>
	);
}
