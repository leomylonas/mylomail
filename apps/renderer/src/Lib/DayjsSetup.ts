import dayjs, { type Dayjs } from "dayjs";
import calendar from "dayjs/plugin/calendar";
import relativeTime from "dayjs/plugin/relativeTime";
import timezone from "dayjs/plugin/timezone";
import utc from "dayjs/plugin/utc";

/**
 * Registered once, on import, so every caller gets the same set of plugins rather than each
 * feature registering its own subset and one of them running before another's plugin loaded.
 */
dayjs.extend(utc);
dayjs.extend(timezone);
dayjs.extend(relativeTime);
dayjs.extend(calendar);

export { dayjs };
export type { Dayjs };
