/**
 * Lays a prepared message out in a shadow root in the app's own page — used for the print copy.
 *
 * The page's content policy is `style-src 'self'`, which blocks every `<style>` element and
 * `style=""` attribute parsed from markup placed in it, and reports a violation each time — even
 * when the markup is parsed in an inert document. So the backend sends the styles separately
 * (`PrintHtmlDto`) and they are applied through the CSSOM, which that directive does not cover.
 * This keeps the page's policy strict instead of loosening it for message content. (The on-screen
 * frame needs none of this: the backend serves it under a policy of its own.)
 *
 * Request-making CSS stays governed by the page's policy, which refuses remote `url()` loads;
 * `@import` is ignored by `replaceSync`.
 */
import type { PrintHtmlDto } from "@mylomail/shared-types/Api/Contracts/PrintHtmlDto";

const marker = "data-inline-style";

export function fillShadowRoot(
	root: ShadowRoot,
	print: PrintHtmlDto,
	extraCss: string,
): void {
	const view = root.ownerDocument.defaultView as
		(Window & { CSSStyleSheet: typeof CSSStyleSheet }) | null;
	if (!view) return;

	// The markup holds no style element and no style attribute, so parsing it reports nothing.
	root.innerHTML = print.html;

	const adopted: CSSStyleSheet[] = [];
	for (const css of [...print.sheets, extraCss]) {
		try {
			const sheet = new view.CSSStyleSheet();
			sheet.replaceSync(css);
			adopted.push(sheet);
		} catch {
			// One malformed stylesheet must not take the rest of the message's styling with it.
		}
	}
	root.adoptedStyleSheets = adopted;

	for (const element of root.querySelectorAll<HTMLElement>(`[${marker}]`)) {
		element.style.cssText =
			print.inline[element.getAttribute(marker) ?? ""] ?? "";
		element.removeAttribute(marker);
	}
}
