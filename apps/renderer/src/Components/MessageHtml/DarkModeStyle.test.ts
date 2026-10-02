import { describe, expect, it } from "vitest";
import {
	darkModeStyle,
	declaresBackground,
} from "@mylomail/renderer/Components/MessageHtml/DarkModeStyle";

describe("declaresBackground", () => {
	it.each([
		'<table bgcolor="#ffffff"><tr><td>x</td></tr></table>',
		'<body background="bg.png">x</body>',
		'<div style="background-color: #fff">x</div>',
		"<style>td { background:#eee }</style><td>x</td>",
		'<div style="BACKGROUND-IMAGE:url(x)">x</div>',
	])("detects an authored background in %s", (html) => {
		expect(declaresBackground(html)).toBe(true);
	});

	it("does not mistake text colour or other properties for a background", () => {
		expect(
			declaresBackground('<p style="color:#333; border: 1px solid">Hi</p>'),
		).toBe(false);
		expect(declaresBackground("<p>The background of this story</p>")).toBe(
			false,
		);
	});
});

describe("darkModeStyle", () => {
	it("does nothing in a light theme", () => {
		expect(darkModeStyle("<p>Hi</p>", false)).toBe("");
	});

	it("does nothing to a message that brings its own background", () => {
		expect(darkModeStyle('<td bgcolor="#fff">Hi</td>', true)).toBe("");
	});

	it("inverts an unstyled message on screen only and restores its images", () => {
		const style = darkModeStyle("<p>Hi</p>", true);
		expect(style).toContain("@media screen");
		expect(style).toContain("html {");
		expect(style).not.toContain("<style");
		expect(style).toMatch(/img[^}]*filter: invert\(1\) hue-rotate\(180deg\)/);
	});

	it("inverts a message with its own background when the reader asks, in any theme", () => {
		const html = '<td bgcolor="#fff">Hi</td>';
		expect(darkModeStyle(html, true, true)).toContain("invert(1)");
		expect(darkModeStyle(html, false, true)).toContain("invert(1)");
	});
});
