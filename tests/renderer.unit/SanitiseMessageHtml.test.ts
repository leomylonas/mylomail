// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { prepare } from "@mylomail/renderer/Components/MessageHtml/SanitiseMessageHtml";

/**
 * Message HTML is the most security-sensitive surface in the app: it is authored by whoever
 * sent the mail, and the renderer displaying it also holds the capability to mutate mail
 * (§13). These are the attacks, not the happy path.
 */
describe("message HTML sanitisation", () => {
	it("keeps ordinary formatting intact", () => {
		const { html } = prepare("<p>Hello <b>there</b></p>", false);

		expect(html).toContain("<b>there</b>");
	});

	it("removes scripts", () => {
		const { html } = prepare('<p>hi</p><script>alert("x")</script>', false);

		expect(html.toLowerCase()).not.toContain("<script");
		expect(html).not.toContain("alert");
	});

	it("removes event handlers", () => {
		const { html } = prepare('<img onerror="alert(1)" src="cid:x">', false);

		expect(html.toLowerCase()).not.toContain("onerror");
	});

	/** A form in a message body exists to collect credentials. There is no benign use. */
	it("removes forms", () => {
		const { html } = prepare(
			'<form action="https://evil.example"><input name="password"></form>',
			false,
		);

		expect(html.toLowerCase()).not.toContain("<form");
	});

	it("removes nested frames", () => {
		const { html } = prepare(
			'<iframe src="https://evil.example"></iframe>',
			false,
		);

		expect(html.toLowerCase()).not.toContain("<iframe");
	});

	it("removes javascript: links", () => {
		const { html } = prepare('<a href="javascript:alert(1)">click</a>', false);

		expect(html.toLowerCase()).not.toContain("javascript:");
	});

	describe("remote content", () => {
		it("withholds remote images and counts them", () => {
			const result = prepare(
				'<img src="https://tracker.example/pixel.gif">',
				false,
			);

			expect(result.html).not.toContain("tracker.example");
			expect(result.blockedRemoteCount).toBe(1);
		});

		/**
		 * §13 is explicit that removing `src` is not enough. A tracking pixel in a background
		 * image works exactly as well as one in an img tag.
		 */
		it("withholds remote content hidden in inline styles", () => {
			const result = prepare(
				'<div style="background: url(https://tracker.example/p.gif)">hi</div>',
				false,
			);

			expect(result.html).not.toContain("tracker.example");
			expect(result.blockedRemoteCount).toBe(1);
		});

		it("withholds srcset, which loads independently of src", () => {
			const result = prepare(
				'<img srcset="https://tracker.example/p.gif 1x" alt="">',
				false,
			);

			expect(result.html).not.toContain("tracker.example");
		});

		it("empties style elements, whose requests live in text rather than attributes", () => {
			const result = prepare(
				"<style>@import url(https://tracker.example/s.css);</style><p>hi</p>",
				false,
			);

			expect(result.html).not.toContain("tracker.example");
		});

		/**
		 * A cid: reference must survive sanitisation, because the blob rewrite happens after
		 * it. Stripping it here left every inline image broken while every unit test passed.
		 */
		it("keeps cid: references for the inline-image rewrite that follows", () => {
			const result = prepare('<img src="cid:logo@example.org">', false);

			expect(result.html).toContain("cid:logo@example.org");
			expect(result.blockedRemoteCount).toBe(0);
		});

		/**
		 * A cid: reference must survive sanitisation, because the blob rewrite runs after it.
		 * Stripping it here left every inline image broken while every unit test passed.
		 */
		it("keeps cid: references for the inline-image rewrite that follows", () => {
			const result = prepare('<img src="cid:logo@example.org">', false);

			expect(result.html).toContain("cid:logo@example.org");
			expect(result.blockedRemoteCount).toBe(0);
		});

		/** Inline parts are already downloaded — they are not remote and must survive. */
		it("keeps already-resolved inline images", () => {
			const result = prepare('<img src="blob:abc123">', false);

			expect(result.html).toContain("blob:abc123");
			expect(result.blockedRemoteCount).toBe(0);
		});

		it("loads remote content once the user asks", () => {
			const result = prepare('<img src="https://example.org/p.gif">', true);

			expect(result.html).toContain("example.org");
		});

		/** Consent to images is never consent to scripts. */
		it("still removes scripts when remote content is allowed", () => {
			const { html } = prepare(
				'<script>alert(1)</script><img src="https://a.example/p">',
				true,
			);

			expect(html.toLowerCase()).not.toContain("<script");
		});
	});
});
