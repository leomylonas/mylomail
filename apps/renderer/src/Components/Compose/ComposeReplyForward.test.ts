// @vitest-environment jsdom
//
// mentionsAttachmentOutsideQuote needs a real DOMParser, same DOM-global exception documented
// in MessageActions.test.ts.
import { describe, expect, it } from "vitest";
import {
	buildReplyRecipients,
	mentionsAttachmentOutsideQuote,
	type MessageReplyContext,
} from "@mylomail/renderer/Components/Compose/ComposeReplyForward";

function context(
	overrides: Partial<MessageReplyContext> = {},
): MessageReplyContext {
	return {
		messageId: "m1",
		from: [{ name: "Alice", email: "alice@example.test" }],
		to: [{ name: "Me", email: "me@example.test" }],
		cc: [],
		replyTo: [],
		subject: "Hello",
		receivedAt: "2026-01-01T00:00:00Z",
		...overrides,
	};
}

describe("buildReplyRecipients", () => {
	it("de-duplicates the reply target even when the source header repeats an address", () => {
		const { to } = buildReplyRecipients(
			"reply",
			context({
				replyTo: [
					{ name: "Alice", email: "Alice@Example.test" },
					{ name: "Alice", email: "alice@example.test" },
				],
			}),
			"me@example.test",
		);

		expect(to).toHaveLength(1);
		expect(to[0]?.email).toBe("Alice@Example.test");
	});

	it("prefers replyTo over from when both are present", () => {
		const { to } = buildReplyRecipients(
			"reply",
			context({ replyTo: [{ name: "List", email: "list@example.test" }] }),
			"me@example.test",
		);

		expect(to.map((a) => a.email)).toEqual(["list@example.test"]);
	});

	it("reply-all merges to/cc, dedupes, excludes self, and never repeats the primary target in cc", () => {
		const { to, cc } = buildReplyRecipients(
			"replyAll",
			context({
				from: [{ name: "Alice", email: "alice@example.test" }],
				to: [
					{ name: "Me", email: "me@example.test" },
					{ name: "Bob", email: "bob@example.test" },
				],
				cc: [
					{ name: "Bob again", email: "bob@example.test" },
					{ name: "Alice", email: "alice@example.test" },
				],
			}),
			"me@example.test",
		);

		expect(to.map((a) => a.email)).toEqual(["alice@example.test"]);
		expect(cc.map((a) => a.email)).toEqual(["bob@example.test"]);
	});

	it("plain reply never populates cc", () => {
		const { cc } = buildReplyRecipients(
			"reply",
			context({ cc: [{ name: "Bob", email: "bob@example.test" }] }),
			"me@example.test",
		);

		expect(cc).toEqual([]);
	});
});

describe("mentionsAttachmentOutsideQuote", () => {
	it("ignores an attachment mention that only appears in quoted/forwarded content", () => {
		const html =
			"<p>Hi there</p>" +
			'<blockquote style="margin:0">Please see the attached invoice.</blockquote>';

		expect(mentionsAttachmentOutsideQuote(html)).toBe(false);
	});

	it("still detects an attachment mention the user actually typed", () => {
		const html =
			"<p>I forgot to attach the file, adding it now.</p>" +
			"<blockquote>Original message</blockquote>";

		expect(mentionsAttachmentOutsideQuote(html)).toBe(true);
	});

	it("detects a mention with no quote present at all", () => {
		expect(mentionsAttachmentOutsideQuote("<p>See attached.</p>")).toBe(true);
	});
});
