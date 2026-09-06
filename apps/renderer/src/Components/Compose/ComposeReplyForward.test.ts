// @vitest-environment jsdom
//
// mentionsAttachmentOutsideQuote needs a real DOMParser, same DOM-global exception documented
// in MessageActions.test.ts.
import { afterEach, describe, expect, it, vi } from "vitest";
import {
	buildForwardSeed,
	buildReplyRecipients,
	buildReplySeed,
	copyAttachments,
	mentionsAttachmentOutsideQuote,
	type ForwardAttachment,
	type MessageReplyContext,
} from "@mylomail/renderer/Components/Compose/ComposeReplyForward";

function context(
	overrides: Partial<MessageReplyContext> = {},
): MessageReplyContext {
	return {
		messageId: "m1",
		accountId: "a1",
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

function attachment(
	id: string,
	filename: string,
	overrides: Partial<Pick<ForwardAttachment, "isInline" | "contentId">> = {},
): ForwardAttachment {
	return {
		id,
		filename,
		mimeType: "application/octet-stream",
		isInline: false,
		contentId: null,
		...overrides,
	};
}

describe("buildReplySeed", () => {
	it("copies only the original's inline attachments, never its ordinary ones", () => {
		const seed = buildReplySeed(
			"reply",
			context(),
			"<p>Hi</p>",
			"me@example.test",
			[
				attachment("inline1", "logo.png", {
					isInline: true,
					contentId: "logo@mylomail.local",
				}),
				attachment("plain1", "report.pdf"),
			],
		);

		expect(seed.attachmentsToCopy?.attachments).toEqual([
			attachment("inline1", "logo.png", {
				isInline: true,
				contentId: "logo@mylomail.local",
			}),
		]);
	});

	it("omits attachmentsToCopy entirely when the original has no inline attachments", () => {
		const seed = buildReplySeed(
			"reply",
			context(),
			"<p>Hi</p>",
			"me@example.test",
			[attachment("plain1", "report.pdf")],
		);

		expect(seed.attachmentsToCopy).toBeUndefined();
	});
});

describe("buildForwardSeed", () => {
	it("copies every attachment, inline and ordinary alike", () => {
		const inline = attachment("inline1", "logo.png", {
			isInline: true,
			contentId: "logo@mylomail.local",
		});
		const plain = attachment("plain1", "report.pdf");

		const seed = buildForwardSeed(context(), "<p>Hi</p>", [inline, plain]);

		expect(seed.attachmentsToCopy?.attachments).toEqual([inline, plain]);
	});
});

describe("copyAttachments", () => {
	afterEach(() => {
		vi.unstubAllGlobals();
	});

	it("copies every attachment independently, reporting only the ones that fail", async () => {
		const fetchMock = vi.fn(async (url: string) => {
			if (url === "/messages/m1/attachments/good") {
				return { ok: true, blob: async () => new Blob(["ok"]) } as Response;
			}
			if (url === "/messages/m1/attachments/bad") {
				return { ok: false } as Response;
			}
			// The upload leg for the one attachment that made it past the fetch above.
			return { ok: true } as Response;
		});
		vi.stubGlobal("fetch", fetchMock);

		const failed = await copyAttachments("draft1", {
			sourceMessageId: "m1",
			attachments: [
				attachment("bad", "missing.pdf"),
				attachment("good", "report.pdf"),
			],
		});

		expect(failed).toEqual(["missing.pdf"]);
		// Both attachments were attempted — a failing one didn't stop the loop early.
		expect(fetchMock).toHaveBeenCalledWith("/messages/m1/attachments/good");
		expect(fetchMock).toHaveBeenCalledWith("/messages/m1/attachments/bad");
		expect(fetchMock).toHaveBeenCalledWith(
			"/drafts/draft1/attachments",
			expect.objectContaining({ method: "POST" }),
		);
	});

	it("carries isInline/contentId onto the upload for an inline attachment", async () => {
		let uploadedForm: FormData | undefined;
		const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
			if (url === "/messages/m1/attachments/inline1") {
				return { ok: true, blob: async () => new Blob(["ok"]) } as Response;
			}
			uploadedForm = init?.body as FormData;
			return { ok: true } as Response;
		});
		vi.stubGlobal("fetch", fetchMock);

		await copyAttachments("draft1", {
			sourceMessageId: "m1",
			attachments: [
				attachment("inline1", "logo.png", {
					isInline: true,
					contentId: "logo@mylomail.local",
				}),
			],
		});

		expect(uploadedForm?.get("isInline")).toBe("true");
		expect(uploadedForm?.get("contentId")).toBe("logo@mylomail.local");
	});

	it("does not set isInline/contentId form fields for an ordinary attachment", async () => {
		let uploadedForm: FormData | undefined;
		const fetchMock = vi.fn(async (url: string, init?: RequestInit) => {
			if (url === "/messages/m1/attachments/plain1") {
				return { ok: true, blob: async () => new Blob(["ok"]) } as Response;
			}
			uploadedForm = init?.body as FormData;
			return { ok: true } as Response;
		});
		vi.stubGlobal("fetch", fetchMock);

		await copyAttachments("draft1", {
			sourceMessageId: "m1",
			attachments: [attachment("plain1", "report.pdf")],
		});

		expect(uploadedForm?.get("isInline")).toBeNull();
		expect(uploadedForm?.get("contentId")).toBeNull();
	});
});
