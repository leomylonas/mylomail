import { useMemo } from "react";
import {
	createCalendarFormatters,
	type CalendarFormatters,
} from "@mylomail/renderer/Components/Calendar/CalendarFormatting";
import { useDateFormats } from "@mylomail/renderer/Shell/DateFormats/DateFormats";

/** The calendar's formatters, on the clock the user chose in Settings. */
export function useCalendarFormatters(): CalendarFormatters {
	const { clock } = useDateFormats();
	return useMemo(() => createCalendarFormatters(undefined, clock), [clock]);
}
