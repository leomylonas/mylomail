import { describe, expect, it } from "vitest";
import {
	fallbackCalendarColours,
	readableTextColour,
	resolveCalendarColour,
} from "@mylomail/renderer/Components/Calendar/CalendarColour";

const hexColour = /^#[0-9a-f]{6}$/;

function relativeLuminance(hex: string): number {
	const [red, green, blue] = [1, 3, 5].map((start) => {
		const channel = Number.parseInt(hex.slice(start, start + 2), 16) / 255;
		return channel <= 0.03928
			? channel / 12.92
			: ((channel + 0.055) / 1.055) ** 2.4;
	});
	return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

function contrastRatio(first: string, second: string): number {
	const [lighter, darker] = [
		relativeLuminance(first),
		relativeLuminance(second),
	].sort((a, b) => b - a);
	return (lighter + 0.05) / (darker + 0.05);
}

const graphColourNames = [
	"lightBlue",
	"lightGreen",
	"lightOrange",
	"lightGray",
	"lightYellow",
	"lightTeal",
	"lightPink",
	"lightBrown",
	"lightRed",
];

const colourlessReports = [
	null,
	undefined,
	"",
	"   ",
	"auto",
	"Auto",
	"maxColor",
	"not-a-colour",
	"#12",
];

describe("resolveCalendarColour", () => {
	it("maps every Graph colour name to its own CSS colour", () => {
		const resolved = graphColourNames.map((name) =>
			resolveCalendarColour(name, "calendar-1"),
		);

		for (const colour of resolved) expect(colour).toMatch(hexColour);
		expect(new Set(resolved).size).toBe(graphColourNames.length);
	});

	it("does not let the calendar id change a named Graph colour", () => {
		expect(resolveCalendarColour("lightBlue", "calendar-1")).toBe(
			resolveCalendarColour("lightBlue", "calendar-2"),
		);
	});

	it("reads the enum name however the server spells it", () => {
		// Kiota's `CalendarColor.ToString()` is PascalCase, which is what the database holds.
		expect(resolveCalendarColour("LightPink", "x")).toBe(
			resolveCalendarColour("lightPink", "x"),
		);
		expect(resolveCalendarColour(" LIGHTBLUE ", "x")).toBe(
			resolveCalendarColour("lightBlue", "x"),
		);
	});

	it("keeps a Google or Graph hex colour, normalised to #rrggbb", () => {
		expect(resolveCalendarColour("#9fe1e7", "cal")).toBe("#9fe1e7");
		expect(resolveCalendarColour("#F83A22", "cal")).toBe("#f83a22");
		expect(resolveCalendarColour("#abc", "cal")).toBe("#aabbcc");
		expect(resolveCalendarColour("#ff0078ff", "cal")).toBe("#ff0078");
	});

	it.each(colourlessReports)(
		"gives %j a palette colour of its own",
		(reported) => {
			expect(fallbackCalendarColours).toContain(
				resolveCalendarColour(reported, "calendar-1"),
			);
		},
	);

	it("gives the same calendar the same fallback every time", () => {
		expect(resolveCalendarColour("Auto", "calendar-1")).toBe(
			resolveCalendarColour(undefined, "calendar-1"),
		);
		expect(resolveCalendarColour("Auto", "calendar-1")).toBe(
			resolveCalendarColour("Auto", "calendar-1"),
		);
	});

	it("spreads colourless calendars across the palette instead of sharing one", () => {
		const ids = Array.from({ length: 40 }, (_, index) => `calendar-${index}`);
		const used = new Set(ids.map((id) => resolveCalendarColour("Auto", id)));

		expect(used.size).toBeGreaterThanOrEqual(
			fallbackCalendarColours.length - 3,
		);
	});
});

describe("readableTextColour", () => {
	it("picks dark text on light backgrounds and light text on dark ones", () => {
		expect(readableTextColour("#ffffff")).toBe("#000000");
		expect(readableTextColour("#f2d03b")).toBe("#000000");
		expect(readableTextColour("#000000")).toBe("#ffffff");
		expect(readableTextColour("#002d9c")).toBe("#ffffff");
	});

	it("reaches WCAG AA text contrast on every colour a calendar can resolve to", () => {
		const backgrounds = [
			...fallbackCalendarColours,
			...graphColourNames.map((name) => resolveCalendarColour(name, "id")),
		];

		for (const background of backgrounds) {
			expect(
				contrastRatio(background, readableTextColour(background)),
			).toBeGreaterThanOrEqual(4.5);
		}
	});
});
