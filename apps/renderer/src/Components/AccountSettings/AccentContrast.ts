/**
 * A contrast floor for the per-account accent colour (§13 Accessibility): "needs a contrast
 * floor, not arbitrary user-picked colour." The colour is rendered as a small swatch against
 * both Carbon's light and dark theme sidebar backgrounds depending on which the user has
 * selected, so a colour picked too close to either extreme — or a low-saturation grey sitting
 * in a mid-lightness "safe band" but still nearly indistinguishable from a mid-grey background
 * — would be effectively invisible in one theme or the other.
 *
 * A lightness-only clamp cannot actually guarantee this: a desaturated grey clamped into a
 * mid-lightness band still measures well under WCAG's 3:1 non-text contrast minimum against
 * Carbon's own g100/g10 backgrounds. This instead computes real WCAG relative-luminance
 * contrast ratios against both reference backgrounds and, only if the original pick fails
 * either one, searches nearby lightness/saturation adjustments — preferring the smallest
 * change from what the user actually picked — for the closest colour that passes both.
 */
const MIN_CONTRAST = 3; // WCAG 2.1 non-text UI component minimum.

// Carbon's own theme background tokens (§13 stack: @carbon/react) — g10 (light) and g100 (dark).
const LIGHT_BG = hexToRgb("#f4f4f4")!;
const DARK_BG = hexToRgb("#161616")!;

/**
 * Adjusts a `#rrggbb` colour, if needed, so it meets {@link MIN_CONTRAST} against both
 * reference backgrounds. Returns the input unchanged if it already does, or if it does not
 * parse as a hex colour at all.
 */
export function ensureAccentContrast(hex: string): string {
	const rgb = parseHex(hex);
	if (!rgb) return hex;
	if (meetsFloor(rgb)) return hex;

	const [h, s, l] = rgbToHsl(rgb);

	// Search lightness first, at the original saturation, then again at full saturation if
	// that alone cannot satisfy both backgrounds (a true grey, s = 0, never can — hue carries
	// no separation from a grey background at any lightness). Smallest |ΔL| wins, so the
	// result stays as close as possible to the colour actually chosen.
	for (const saturation of [s, 1]) {
		let best: { l: number; delta: number } | null = null;
		for (let candidate = 0; candidate <= 1; candidate += 0.01) {
			const candidateRgb = hslToRgb(h, saturation, candidate);
			if (!meetsFloor(candidateRgb)) continue;
			const delta = Math.abs(candidate - l);
			if (!best || delta < best.delta) {
				best = { l: candidate, delta };
			}
		}
		if (best) {
			return rgbToHex(hslToRgb(h, saturation, best.l));
		}
	}

	// Nothing at this hue clears both backgrounds even at full saturation (only possible for
	// hues WCAG itself treats as inherently low-contrast, e.g. pure yellow) — fully saturated,
	// maximally-separated mid grey-adjacent fallback beats leaving the floor unenforced.
	return rgbToHex(hslToRgb(h, 1, 0.5));
}

function meetsFloor(rgb: [number, number, number]): boolean {
	const luminance = relativeLuminance(rgb);
	return (
		contrastRatio(luminance, relativeLuminance(LIGHT_BG)) >= MIN_CONTRAST &&
		contrastRatio(luminance, relativeLuminance(DARK_BG)) >= MIN_CONTRAST
	);
}

/** WCAG 2.1 relative luminance. */
function relativeLuminance([r, g, b]: [number, number, number]): number {
	const linear = (channel: number) => {
		const c = channel / 255;
		return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
	};
	return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

/** WCAG 2.1 contrast ratio between two relative luminances. */
function contrastRatio(l1: number, l2: number): number {
	const lighter = Math.max(l1, l2);
	const darker = Math.min(l1, l2);
	return (lighter + 0.05) / (darker + 0.05);
}

function hexToRgb(hex: string): [number, number, number] | null {
	return parseHex(hex);
}

function rgbToHex([r, g, b]: [number, number, number]): string {
	return toHex(Math.round(r), Math.round(g), Math.round(b));
}

function hslToRgb(h: number, s: number, l: number): [number, number, number] {
	const hex = hslToHex(h, s, l);
	return parseHex(hex)!;
}

function parseHex(hex: string): [number, number, number] | null {
	const match = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
	if (!match) return null;
	const value = match[1];
	return [
		parseInt(value.slice(0, 2), 16),
		parseInt(value.slice(2, 4), 16),
		parseInt(value.slice(4, 6), 16),
	];
}

function rgbToHsl([r, g, b]: [number, number, number]): [
	number,
	number,
	number,
] {
	const rn = r / 255;
	const gn = g / 255;
	const bn = b / 255;
	const max = Math.max(rn, gn, bn);
	const min = Math.min(rn, gn, bn);
	const l = (max + min) / 2;

	if (max === min) return [0, 0, l];

	const d = max - min;
	const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
	let h: number;
	switch (max) {
		case rn:
			h = (gn - bn) / d + (gn < bn ? 6 : 0);
			break;
		case gn:
			h = (bn - rn) / d + 2;
			break;
		default:
			h = (rn - gn) / d + 4;
	}
	return [h / 6, s, l];
}

function hslToHex(h: number, s: number, l: number): string {
	if (s === 0) {
		const gray = Math.round(l * 255);
		return toHex(gray, gray, gray);
	}

	const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
	const p = 2 * l - q;
	const r = hueToRgb(p, q, h + 1 / 3);
	const g = hueToRgb(p, q, h);
	const b = hueToRgb(p, q, h - 1 / 3);
	return toHex(Math.round(r * 255), Math.round(g * 255), Math.round(b * 255));
}

function hueToRgb(p: number, q: number, tInput: number): number {
	let t = tInput;
	if (t < 0) t += 1;
	if (t > 1) t -= 1;
	if (t < 1 / 6) return p + (q - p) * 6 * t;
	if (t < 1 / 2) return q;
	if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
	return p;
}

function toHex(r: number, g: number, b: number): string {
	const component = (value: number) => value.toString(16).padStart(2, "0");
	return `#${component(r)}${component(g)}${component(b)}`;
}
