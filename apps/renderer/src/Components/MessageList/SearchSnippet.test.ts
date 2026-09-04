import { describe, expect, it } from "vitest";
import { parseSearchSnippet } from "@mylomail/renderer/Components/MessageList/SearchSnippet";

const start = "";
const end = "";

describe("parseSearchSnippet", () => {
	it("returns a single plain segment when nothing matched", () => {
		expect(parseSearchSnippet("no markers here")).toEqual([
			{ text: "no markers here", highlighted: false },
		]);
	});

	it("splits text around one highlighted term", () => {
		expect(
			parseSearchSnippet(`please review the ${start}invoice${end} attached`),
		).toEqual([
			{ text: "please review the ", highlighted: false },
			{ text: "invoice", highlighted: true },
			{ text: " attached", highlighted: false },
		]);
	});

	it("handles multiple highlighted terms", () => {
		expect(
			parseSearchSnippet(`${start}invoice${end} due ${start}Friday${end}`),
		).toEqual([
			{ text: "invoice", highlighted: true },
			{ text: " due ", highlighted: false },
			{ text: "Friday", highlighted: true },
		]);
	});

	it("treats an unterminated marker as plain text rather than dropping it", () => {
		expect(parseSearchSnippet(`before ${start}unterminated`)).toEqual([
			{ text: "before ", highlighted: false },
			{ text: "unterminated", highlighted: false },
		]);
	});

	it("returns an empty array for an empty snippet", () => {
		expect(parseSearchSnippet("")).toEqual([]);
	});
});
