/**
 * How times are written. `system` leaves the clock to the OS locale; the other two override it
 * wherever a time is shown, so one setting reaches every date and time in the app.
 */
export type ClockPreference = "system" | "12h" | "24h";

/** Matches server/MyloMail.Api/Domain/AppSettings.cs's TimeFormatPreference ordinals. */
export function clockPreferenceFromSetting(
	value: number | undefined,
): ClockPreference {
	return value === 1 ? "12h" : value === 2 ? "24h" : "system";
}

/** The `Intl.DateTimeFormat` options that select the clock; empty for the locale's own. */
export function clockOptions(
	preference: ClockPreference,
): Intl.DateTimeFormatOptions {
	switch (preference) {
		case "12h":
			return { hour12: true };
		case "24h":
			// h23 rather than hour12:false, which a few locales resolve to h24 and print midnight as 24:00.
			return { hourCycle: "h23" };
		default:
			return {};
	}
}

/** Whether times will print on a 24-hour clock under this preference and locale. */
export function uses24HourClock(
	preference: ClockPreference,
	locales?: Intl.LocalesArgument,
): boolean {
	if (preference !== "system") return preference === "24h";
	return ["h23", "h24"].includes(
		new Intl.DateTimeFormat(locales, { hour: "numeric" }).resolvedOptions()
			.hourCycle ?? "",
	);
}

export interface DateFormats {
	readonly clock: ClockPreference;
	/** Time of day: "4:05 PM" or "16:05". */
	time(date: Date): string;
	/** A date with its time: "10/13/26, 4:05 PM". */
	dateTime(date: Date): string;
	/** A longer date with its time, for headers: "Oct 13, 2026, 4:05 PM". */
	dateTimeMedium(date: Date): string;
}

export function createDateFormats(
	preference: ClockPreference,
	locales?: Intl.LocalesArgument,
): DateFormats {
	const clock = clockOptions(preference);
	const time = new Intl.DateTimeFormat(locales, {
		hour: "numeric",
		minute: "2-digit",
		...clock,
	});
	const dateTime = new Intl.DateTimeFormat(locales, {
		dateStyle: "short",
		timeStyle: "short",
		...clock,
	});
	const dateTimeMedium = new Intl.DateTimeFormat(locales, {
		dateStyle: "medium",
		timeStyle: "short",
		...clock,
	});
	return {
		clock: preference,
		time: (date) => time.format(date),
		dateTime: (date) => dateTime.format(date),
		dateTimeMedium: (date) => dateTimeMedium.format(date),
	};
}

/** The OS locale's own clock, for code that runs before the preference has loaded. */
export const systemDateFormats = createDateFormats("system");
