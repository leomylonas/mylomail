/**
 * `MessageSummaryDto.SearchSnippet` (§8) delimits FTS5's matched terms with ASCII SOH/STX
 * control characters, never HTML — the underlying text is arbitrary, untrusted sender content,
 * and must never reach the DOM as markup. This splits it into plain-text segments the caller
 * renders itself (e.g. a `<mark>` around each highlighted one), so nothing here ever touches
 * `dangerouslySetInnerHTML`.
 */
const highlightStart = "";
const highlightEnd = "";

export interface SnippetSegment {
	text: string;
	highlighted: boolean;
}

export function parseSearchSnippet(snippet: string): SnippetSegment[] {
	const segments: SnippetSegment[] = [];
	let rest = snippet;

	while (rest.length > 0) {
		const start = rest.indexOf(highlightStart);
		if (start === -1) {
			segments.push({ text: rest, highlighted: false });
			break;
		}

		if (start > 0) {
			segments.push({ text: rest.slice(0, start), highlighted: false });
		}

		const end = rest.indexOf(highlightEnd, start + highlightStart.length);
		if (end === -1) {
			// An unterminated marker is a malformed snippet, not something to crash over —
			// treat the rest as plain text rather than silently dropping it.
			segments.push({
				text: rest.slice(start + highlightStart.length),
				highlighted: false,
			});
			break;
		}

		segments.push({
			text: rest.slice(start + highlightStart.length, end),
			highlighted: true,
		});
		rest = rest.slice(end + highlightEnd.length);
	}

	return segments;
}
