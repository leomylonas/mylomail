import { describe, expect, it } from "vitest";
import { ensureAccentContrast } from "@mylomail/renderer/Components/AccountSettings/AccentContrast";

const LIGHT_BG: [number, number, number] = [0xf4, 0xf4, 0xf4];
const DARK_BG: [number, number, number] = [0x16, 0x16, 0x16];
const MIN_CONTRAST = 3;

function hexToRgb(hex: string): [number, number, number] {
	const value = hex.replace("#", "");
	return [
		parseInt(value.slice(0, 2), 16),
		parseInt(value.slice(2, 4), 16),
		parseInt(value.slice(4, 6), 16),
	];
}

function relativeLuminance([r, g, b]: [number, number, number]): number {
	const linear = (channel: number) => {
		const c = channel / 255;
		return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
	};
	return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

function contrastRatio(
	a: [number, number, number],
	b: [number, number, number],
): number {
	const l1 = relativeLuminance(a);
	const l2 = relativeLuminance(b);
	const lighter = Math.max(l1, l2);
	const darker = Math.min(l1, l2);
	return (lighter + 0.05) / (darker + 0.05);
}

function assertMeetsFloor(hex: string) {
	const rgb = hexToRgb(hex);
	expect(contrastRatio(rgb, LIGHT_BG)).toBeGreaterThanOrEqual(
		MIN_CONTRAST - 1e-6,
	);
	expect(contrastRatio(rgb, DARK_BG)).toBeGreaterThanOrEqual(
		MIN_CONTRAST - 1e-6,
	);
}

describe("ensureAccentContrast", () => {
	it("leaves a colour that already clears both backgrounds unchanged", () => {
		expect(ensureAccentContrast("#3b82f6")).toBe("#3b82f6");
	});

	it("adjusts a near-black pick to clear both backgrounds", () => {
		const result = ensureAccentContrast("#050505");
		assertMeetsFloor(result);
	});

	it("adjusts a near-white pick to clear both backgrounds", () => {
		const result = ensureAccentContrast("#fdfdfd");
		assertMeetsFloor(result);
	});

	it("adjusts a low-saturation mid-grey that a lightness-only clamp would have let through", () => {
		// #333333: mid-lightness, zero saturation — the exact case a naive lightness clamp
		// misses, since it already sits inside any reasonable "safe" lightness band while
		// still measuring well under 3:1 against Carbon's dark background.
		const result = ensureAccentContrast("#333333");
		assertMeetsFloor(result);
	});

	it("preserves hue where possible while adjusting for contrast", () => {
		const result = ensureAccentContrast("#100000");
		const [r, g, b] = hexToRgb(result);
		expect(r).toBeGreaterThan(g);
		expect(r).toBeGreaterThan(b);
	});

	it("passes through an unparseable value unchanged", () => {
		expect(ensureAccentContrast("not-a-colour")).toBe("not-a-colour");
	});
});
