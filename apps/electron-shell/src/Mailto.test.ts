import { describe, expect, it } from "vitest";
import {
	extractMailtoUris,
	mailtoWindowQuery,
	parseMailtoUri,
} from "@mylomail/electron-shell/Mailto";

describe("mailto activation", () => {
	it("parses path and case-insensitive query recipients", () => {
		expect(
			parseMailtoUri(
				"MAILTO:first%40example.test,second@example.test?CC=copy%40example.test&cc=other%40example.test&Bcc=blind%40example.test&subject=Hello%20there&body=Line%201%0ALine%202",
			),
		).toEqual({
			to: ["first@example.test", "second@example.test"],
			cc: ["copy@example.test", "other@example.test"],
			bcc: ["blind@example.test"],
			subject: "Hello there",
			body: "Line 1\nLine 2",
		});
	});

	it("deduplicates recipients and removes header newlines", () => {
		expect(
			parseMailtoUri(
				"mailto:person@example.test?to=PERSON%40example.test&subject=First%0ASecond&cc=copy%40example.test%0ABcc%3Ahidden%40example.test",
			),
		).toEqual({
			to: ["person@example.test"],
			cc: ["copy@example.test Bcc:hidden@example.test"],
			bcc: [],
			subject: "First Second",
			body: "",
		});
	});

	it("ignores non-mailto launch arguments and rejects malformed encoding", () => {
		expect(
			extractMailtoUris([
				"electron",
				"--no-sandbox",
				"https://example.test",
				"mailto:valid@example.test",
				"mailto:bad%encoding",
			]),
		).toEqual(["mailto:valid@example.test"]);
	});

	it("round-trips the URI through the internal window query", () => {
		const uri = "mailto:person@example.test?subject=A%20%26%20B";
		expect(new URLSearchParams(mailtoWindowQuery(uri)).get("mailto")).toBe(uri);
	});
});
