import { beforeEach, describe, expect, it, vi } from "vitest";
import { buildMailtoSeed } from "@mylomail/renderer/Components/Compose/ComposeMailto";

describe("mailto compose seed", () => {
	beforeEach(() => {
		vi.spyOn(crypto, "randomUUID").mockReturnValue(
			"00000000-0000-4000-8000-000000000000",
		);
	});

	it("prefills recipients and escapes a multiline plain-text body", () => {
		expect(
			buildMailtoSeed({
				to: ["to@example.test"],
				cc: ["copy@example.test"],
				bcc: ["blind@example.test"],
				subject: "A subject",
				body: "One <two> & three\nSecond line",
			}),
		).toEqual({
			key: "mailto-00000000-0000-4000-8000-000000000000",
			to: [{ name: null, email: "to@example.test" }],
			cc: [{ name: null, email: "copy@example.test" }],
			bcc: [{ name: null, email: "blind@example.test" }],
			subject: "A subject",
			bodyHtml: "<p>One &lt;two&gt; &amp; three<br>Second line</p>",
			inReplyToMessageId: null,
		});
	});
});
