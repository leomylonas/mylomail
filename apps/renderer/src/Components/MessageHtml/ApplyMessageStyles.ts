/**
 * Applies a message's own styling, plus the renderer's additions, inside the isolated frame (or,
 * for printing, inside a shadow root that keeps the message's CSS from reaching the app).
 *
 * The frame is a `srcdoc` document, which inherits the renderer's `style-src 'self'`. That
 * blocks every `<style>` element and `style=""` attribute parsed from the message's markup, so
 * a message rendered as bare, unstyled HTML. Setting styles through the CSSOM is not covered by
 * that directive, so the message's stylesheets and inline declarations are re-applied that way
 * — which keeps the renderer's policy strict rather than loosening it for message content.
 *
 * Request-making CSS stays governed by the frame's own policy (`default-src 'none'`), which
 * refuses remote `url()` loads until the user allows them; `@import` is ignored by
 * `replaceSync`.
 */
export function applyMessageStyles(
	scope: Document | ShadowRoot,
	extraCss: string,
): void {
	// `instanceof` is unreliable across the frame boundary: the frame's Document is another realm's.
	const owner =
		scope.nodeType === 9 ? (scope as Document) : scope.ownerDocument;
	const view = owner?.defaultView as
		(Window & { CSSStyleSheet: typeof CSSStyleSheet }) | null;
	if (!view) return;

	const sheets: CSSStyleSheet[] = [];
	const add = (css: string) => {
		try {
			const sheet = new view.CSSStyleSheet();
			sheet.replaceSync(css);
			sheets.push(sheet);
		} catch {
			// One malformed stylesheet must not take the rest of the message's styling with it.
		}
	};
	for (const element of scope.querySelectorAll("style"))
		add(element.textContent ?? "");
	add(extraCss);
	scope.adoptedStyleSheets = sheets;

	for (const element of scope.querySelectorAll<HTMLElement>("[style]"))
		element.style.cssText = element.getAttribute("style") ?? "";
}
