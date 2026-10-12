/**
 * Applies a message's own styling, plus the renderer's additions, inside a shadow root that keeps
 * the message's CSS from reaching the app — used for the print copy.
 *
 * The shadow root lives in the app's own page, whose policy is `style-src 'self'`. That blocks
 * every `<style>` element and `style=""` attribute parsed from the message's markup, so a message
 * rendered there as bare, unstyled HTML. Setting styles through the CSSOM is not covered by that
 * directive, so the message's stylesheets and inline declarations are re-applied that way — which
 * keeps the app's policy strict rather than loosening it for message content. (The on-screen
 * frame needs none of this: it is served by the backend under a policy of its own.)
 *
 * Request-making CSS stays governed by the page's policy, which refuses remote `url()` loads;
 * `@import` is ignored by `replaceSync`.
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
