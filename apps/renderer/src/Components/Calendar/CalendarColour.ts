/**
 * Turns whatever colour a provider reported for a calendar into one CSS `#rrggbb` value
 * (§13 Epic 7), so the month grid, the agenda and the sidebar swatch always agree.
 *
 * Providers disagree about what a calendar colour even is:
 *
 * - Google sends `backgroundColor` as a hex string (`#9fe1e7`).
 * - Microsoft Graph sends a `color` enum *name* (`auto`, `lightBlue`, …), which is not a CSS
 *   colour — `Auto` or `LightOrange` set as a custom property leaves `background` invalid, so the
 *   chip rendered transparent — and, where it has one, an exact `hexColor`. The server stores
 *   the hex when Graph supplies it and the enum name otherwise (Kiota spells it `LightBlue`).
 * - CalDAV and local calendars send nothing.
 *
 * A calendar with no usable colour gets a deterministic one from a small palette, keyed on its
 * id, so it keeps the same colour on every launch and different calendars tend to differ —
 * instead of every unstyled calendar sharing one colour, or inheriting its account's.
 */

/** Outlook's palette buckets. `auto` and `maxColor` (a sentinel) carry no colour of their own. */
const graphColours: Readonly<Record<string, string>> = {
	lightblue: "#5ba4e6",
	lightgreen: "#6dbf67",
	lightorange: "#f1a343",
	lightgray: "#a5a9ad",
	lightyellow: "#f2d03b",
	lightteal: "#4cc4be",
	lightpink: "#ee8cc0",
	lightbrown: "#b98b65",
	lightred: "#e5706b",
};

/**
 * Mid-tone, saturated hues drawn from Carbon's categorical data-visualisation palette: each is
 * distinguishable from the others and keeps a readable contrast on both the light and dark
 * theme backgrounds.
 */
export const fallbackCalendarColours: readonly string[] = [
	"#6929c4",
	"#1192e8",
	"#005d5d",
	"#9f1853",
	"#fa4d56",
	"#198038",
	"#002d9c",
	"#ee538b",
	"#b28600",
	"#009d9a",
	"#8a3800",
	"#a56eff",
];

const lightText = "#ffffff";
// Pure black rather than Carbon's gray-100: with black, whichever of the two texts contrasts
// more is at least 4.58:1 against any background; gray-100 dips to 4.25:1 mid-range.
const darkText = "#000000";

/** A stable 32-bit FNV-1a hash of the calendar id, so the fallback never changes between runs. */
function hashCalendarId(calendarId: string): number {
	let hash = 0x811c9dc5;
	for (let index = 0; index < calendarId.length; index += 1) {
		hash ^= calendarId.charCodeAt(index);
		hash = Math.imul(hash, 0x01000193);
	}
	return hash >>> 0;
}

/** `#rgb`, `#rrggbb` or `#rrggbbaa` as lowercase `#rrggbb`; anything else is `null`. */
function normaliseHex(value: string): string | null {
	const match = /^#([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$/i.exec(value);
	if (!match) return null;

	const digits = match[1].toLowerCase();
	if (digits.length === 3) {
		return `#${digits[0]}${digits[0]}${digits[1]}${digits[1]}${digits[2]}${digits[2]}`;
	}
	return `#${digits.slice(0, 6)}`;
}

/** The colour to draw a calendar in; always a lowercase `#rrggbb`. */
export function resolveCalendarColour(
	reported: string | null | undefined,
	calendarId: string,
): string {
	const value = reported?.trim() ?? "";
	const hex = normaliseHex(value);
	if (hex) return hex;

	const named = graphColours[value.toLowerCase()];
	if (named) return named;

	return fallbackCalendarColours[
		hashCalendarId(calendarId) % fallbackCalendarColours.length
	];
}

function channelLuminance(channel: number): number {
	const scaled = channel / 255;
	return scaled <= 0.03928 ? scaled / 12.92 : ((scaled + 0.055) / 1.055) ** 2.4;
}

/**
 * Light or dark text, whichever contrasts more with `background` (WCAG relative luminance).
 * Anything that is not a resolved `#rrggbb` gets light text, the colour the chips always used.
 */
export function readableTextColour(background: string): string {
	const hex = normaliseHex(background);
	if (!hex) return lightText;

	const luminance =
		0.2126 * channelLuminance(Number.parseInt(hex.slice(1, 3), 16)) +
		0.7152 * channelLuminance(Number.parseInt(hex.slice(3, 5), 16)) +
		0.0722 * channelLuminance(Number.parseInt(hex.slice(5, 7), 16));

	const contrastWithLight = 1.05 / (luminance + 0.05);
	const contrastWithDark = (luminance + 0.05) / 0.05;
	return contrastWithLight >= contrastWithDark ? lightText : darkText;
}
