import type { ComponentProps, ReactElement, ReactNode } from "react";
import { Tooltip } from "@carbon/react";

/**
 * Carbon's tooltip for hover and focus hints, in place of a `title` attribute (which the browser
 * draws in its own unthemed style, after its own delay, and which no keyboard user ever sees).
 * Without a hint it renders the child as it is.
 */
export function Hint({
	hint,
	children,
	align = "top",
}: {
	hint: ReactNode;
	children: ComponentProps<typeof Tooltip>["children"];
	align?: "top" | "bottom" | "left" | "right" | "top-start" | "bottom-start";
}) {
	if (hint === undefined || hint === null || hint === "" || hint === false)
		return children as ReactElement;
	return (
		<Tooltip label={hint} align={align} enterDelayMs={300}>
			{children}
		</Tooltip>
	);
}

/** A multi-line hint from lines of text. */
export function HintLines({
	lines,
}: {
	lines: readonly (string | undefined)[];
}) {
	return (
		<>
			{lines.filter(Boolean).map((line, index) => (
				<div key={index}>{line}</div>
			))}
		</>
	);
}
