// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { isBlankComposeBody } from "@mylomail/renderer/Components/Compose/Compose";

describe("isBlankComposeBody", () => {
	it("is blank when empty or when only the signature compose added is there", () => {
		expect(isBlankComposeBody("")).toBe(true);
		expect(isBlankComposeBody("<p><br></p>")).toBe(true);
		expect(
			isBlankComposeBody(
				'<p><br></p><div data-mylomail-signature="id"><p>Cheers, Leo</p></div>',
			),
		).toBe(true);
	});

	it("is not blank once the user has written anything or added an image", () => {
		expect(isBlankComposeBody("<p>Hi</p>")).toBe(false);
		expect(
			isBlankComposeBody(
				'<p>Hi</p><div data-mylomail-signature="id"><p>Cheers</p></div>',
			),
		).toBe(false);
		expect(isBlankComposeBody('<p><img src="cid:x"></p>')).toBe(false);
	});
});
