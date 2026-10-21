import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendRawMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

/** The Basic tier of the local matrix (`pnpm imap:up`), shared with the other compose specs. */
const imapPort = 13143;

const probed = [
	"p",
	"h1",
	"h2",
	"h3",
	"blockquote",
	"ul",
	"ol",
	"li",
	"sub",
	"sup",
] as const;

/** Inline formatting is carried by different elements on each side; the text's own look is compared. */
const formatted = ["bold", "italic", "underline", "strike"] as const;

const properties = [
	"display",
	"marginTop",
	"marginBottom",
	"marginLeft",
	"paddingLeft",
	"fontFamily",
	"fontSize",
	"fontWeight",
	"fontStyle",
	"lineHeight",
	"listStyleType",
	"verticalAlign",
	"textDecorationLine",
] as const;

/**
 * Writing and reading must agree: the compose editor and the reading pane show the same message
 * with the same spacing and type, because what the author sees is what the recipient gets.
 *
 * A reply quotes a received message into the editor, so one message is rendered both ways and
 * the computed style of every element is compared, rather than the two stylesheets being
 * compared by eye. A new element or property that one side styles and the other forgets fails here.
 */
test("the compose editor shows message content the way the reading pane does", async () => {
	const subject = `Parity ${Date.now()}`;
	const html = [
		"<p>First paragraph with <strong>bold</strong>, <em>italic</em>, <u>underline</u> and <s>strike</s>.</p>",
		"<p>Second paragraph with H<sub>2</sub>O and x<sup>2</sup>.</p>",
		"<h1>Heading one</h1><h2>Heading two</h2><h3>Heading three</h3>",
		"<ul><li>Bullet one</li><li>Bullet two</li></ul>",
		"<ol><li>Number one</li><li>Number two</li></ol>",
		"<blockquote><p>Quoted words</p></blockquote>",
		"<p>Last paragraph.</p>",
	].join("");
	const message = [
		"From: Someone <sender@example.org>",
		"To: test@mylomail.local",
		`Subject: ${subject}`,
		`Message-ID: <${subject.replace(/\W+/g, "-")}@example.org>`,
		`Date: ${new Date().toUTCString()}`,
		"MIME-Version: 1.0",
		'Content-Type: text/html; charset="utf-8"',
		"",
		`<html><body>${html}</body></html>`,
		"",
	].join("\r\n");
	await clearInbox(imapPort);
	await appendRawMessage(imapPort, "test@mylomail.local", message, new Date());

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await window
			.getByRole("button", { name: /inbox/i })
			.first()
			.click({ timeout: 60_000 });
		await window
			.getByRole("button", { name: new RegExp(subject) })
			.click({ timeout: 60_000 });
		const frame = window.frameLocator('iframe[title="Message body"]');
		await expect(frame.locator("h1")).toBeVisible({ timeout: 60_000 });

		const read = await frame.locator("body").evaluate(
			(body, args) => {
				const result: Record<string, Record<string, string>[]> = {};
				for (const tag of args.probed) {
					result[tag] = [...body.querySelectorAll(tag)].map((element) => {
						const style = getComputedStyle(element);
						return Object.fromEntries(
							args.properties.map((name) => [
								name,
								String(style[name as keyof CSSStyleDeclaration]),
							]),
						);
					});
				}
				return result;
			},
			{ probed: [...probed], properties: [...properties] },
		);

		const inline = (root: HTMLElement, words: readonly string[]) => {
			const result: Record<string, Record<string, string>> = {};
			for (const word of words) {
				const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
				let found: HTMLElement | null = null;
				for (let node = walker.nextNode(); node; node = walker.nextNode()) {
					if (node.textContent === word) found = node.parentElement;
				}
				const style = found ? getComputedStyle(found) : null;
				result[word] = {
					fontWeight: style?.fontWeight ?? "missing",
					fontStyle: style?.fontStyle ?? "missing",
					textDecorationLine: style?.textDecorationLine ?? "missing",
				};
			}
			return result;
		};
		const readInline = await frame
			.locator("body")
			.evaluate(inline, [...formatted]);

		await window
			.getByRole("button", { name: "Reply", exact: true })
			.first()
			.click();
		const editor = window.getByLabel("Message", { exact: true });
		await expect(editor.locator("h1")).toBeVisible({ timeout: 60_000 });

		const written = await editor.evaluate(
			(root, args) => {
				const result: Record<string, Record<string, string>[]> = {};
				for (const tag of args.probed) {
					result[tag] = [...root.querySelectorAll(tag)].map((element) => {
						const style = getComputedStyle(element);
						return Object.fromEntries(
							args.properties.map((name) => [
								name,
								String(style[name as keyof CSSStyleDeclaration]),
							]),
						);
					});
				}
				return result;
			},
			{ probed: [...probed], properties: [...properties] },
		);

		const writtenInline = await editor.evaluate(inline, [...formatted]);
		expect(
			writtenInline,
			"bold, italic, underline and strikethrough look the same when writing as when reading",
		).toEqual(readInline);
		expect(readInline.underline?.textDecorationLine).toContain("underline");
		expect(readInline.strike?.textDecorationLine).toContain("line-through");

		for (const tag of probed) {
			expect(
				read[tag]?.length,
				`<${tag}> is in the message the reply quotes`,
			).toBeGreaterThan(0);
			// The reply quotes the message inside a blockquote of its own, so the editor may hold
			// the same elements after it: compare the ones that correspond, in order.
			const writtenTail = (written[tag] ?? []).slice(-(read[tag]?.length ?? 0));
			expect(
				writtenTail,
				`<${tag}> is styled the same when writing as when reading`,
			).toEqual(read[tag]);
		}
	} finally {
		await app.close();
	}
});
