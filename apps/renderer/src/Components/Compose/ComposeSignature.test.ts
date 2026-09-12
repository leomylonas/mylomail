// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { applyIdentitySignature } from "@mylomail/renderer/Components/Compose/ComposeSignature";

const first = {
	id: "first",
	signatureHtml: "<p>Regards,<br>Alice</p>",
};
const second = {
	id: "second",
	signatureHtml: "<p>Thanks,<br>Support</p>",
};

describe("applyIdentitySignature", () => {
	it("replaces an edited managed signature without changing authored or quoted content", () => {
		const initial = applyIdentitySignature(
			"<p>My answer</p><blockquote><p>Original</p></blockquote>",
			first,
		);
		const edited = initial.replace("Regards,", "Warm regards,");

		const changed = applyIdentitySignature(edited, second);

		expect(changed).toContain("<p>My answer</p>");
		expect(changed).toContain("<blockquote><p>Original</p></blockquote>");
		expect(changed).toContain("Thanks,<br>Support");
		expect(changed).not.toContain("Warm regards,");
		expect(changed.match(/data-mylomail-signature/g)).toHaveLength(1);
	});

	it("removes the old signature when the selected identity has none", () => {
		const initial = applyIdentitySignature("<p>Message</p>", first);

		expect(
			applyIdentitySignature(initial, { id: "plain", signatureHtml: null }),
		).toBe("<p>Message</p>");
	});

	it("inserts a signature when the previous identity had none", () => {
		const changed = applyIdentitySignature("<p>Message</p>", second);

		expect(changed).toBe(
			'<p>Message</p><p><br></p><div data-mylomail-signature="second"><p>Thanks,<br>Support</p></div>',
		);
	});
});
