/**
 * Whether the message sets any background of its own — a `bgcolor` attribute, a `background`
 * attribute, or a `background`/`background-color`/`background-image` declaration. Such a
 * message was designed against that background and is shown as authored.
 */
export function declaresBackground(html: string): boolean {
	return /\bbgcolor\s*=|\bbackground\s*=|\bbackground(?:-color|-image)?\s*:/i.test(
		html,
	);
}

/**
 * On-screen CSS (a bare stylesheet, not a `<style>` element — see `applyMessageStyles`) that keeps a message with no background of its own from glaring white in a
 * dark theme, or an empty string when nothing needs doing. `forceInvert` is the reader asking
 * for it explicitly.
 *
 * The whole document is inverted and hue-rotated back, so the text colours the author chose
 * for a white page (dark grey, dark blue links) flip to something readable instead of sitting
 * unreadably on a dark canvas, and images, video and SVG are inverted a second time to
 * restore them. The canvas colour is the exact inverse of the dark pane background, so the
 * message blends into it. A message that declares its own background is left alone: inverting
 * it would mangle a design that was made for that colour.
 */
export function darkModeStyle(
	html: string,
	dark: boolean,
	forceInvert = false,
): string {
	// Forced by the reader for this one view: applies in any theme, and to a message that
	// brings its own background, which is the case the automatic mode leaves alone.
	if (!forceInvert && (!dark || declaresBackground(html))) return "";
	const restore = "invert(1) hue-rotate(180deg)";
	return `@media screen { html { background: ${darkCanvasInverse}; filter: ${restore}; } img, picture, video, canvas, svg, iframe { filter: ${restore}; } }`;
}

/** The inverse (`255 - c`) of Carbon g100's `--cds-background`, #161616. */
const darkCanvasInverse = "#e9e9e9";
