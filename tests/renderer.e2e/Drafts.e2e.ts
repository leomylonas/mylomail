import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	bodiesIn,
	clearFolder,
	countWithSubject,
} from "@mylomail/renderer-e2e/SeedImap";

/**
 * The QRESYNC tier, shared with MailFlow but in a different folder.
 *
 * Drafts land in Drafts and MailFlow works in INBOX, so the two cannot disturb each other —
 * and adding a fourth Dovecot instance for one folder would be waste.
 */
const imapPort = 11143;

/**
 * A draft saved here reaches the server's Drafts folder.
 *
 * The point of server-side drafts is that a draft started on this device appears on another,
 * which only the server can attest to. Asserting the local row would test nothing that the
 * unit tests do not already cover.
 */
test("a saved draft is stored in the server's Drafts folder", async () => {
	await clearFolder(imapPort, "Drafts");

	const { app, window } = await launchApp();
	const subject = `Draft ${Date.now()}`;

	try {
		await createImapAccount(window, imapPort);

		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: "New message" }).click();

		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill(subject);
		await window
			.getByLabel("Message", { exact: true })
			.fill("Still writing this.");
		await window.getByRole("button", { name: "Save draft" }).click();

		// Waits for the first version specifically, not merely for the subject to appear. The
		// push is a background job and coalesces saves, so without pinning the first version
		// down the second edit can be folded into a single push — and then the update path
		// below is never exercised at all.
		await expect
			.poll(
				async () =>
					(await bodiesIn(imapPort, "Drafts")).includes("Still writing this."),
				{
					timeout: 60_000,
					message: "the draft never reached the server",
				},
			)
			.toBe(true);

		// Saving again replaces the server's copy rather than appending a second one: on IMAP
		// an update is an append plus an expunge, and getting that order wrong leaves two.
		await window
			.getByLabel("Message", { exact: true })
			.fill("Still writing this, with more.");
		await window.getByRole("button", { name: "Save draft" }).click();

		await expect
			.poll(async () => countWithSubject(imapPort, "Drafts", subject), {
				timeout: 60_000,
				message: "saving twice left two copies on the server",
			})
			.toBe(1);
	} finally {
		await app.close();
	}
});
