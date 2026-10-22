import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendMessage,
	clearInbox,
	countWithSubject,
} from "@mylomail/renderer-e2e/SeedImap";

/** Two tiers of the local matrix, so the two accounts are on different servers. */
const sourcePort = 13143;
const targetPort = 12143;

/**
 * Dragging a message onto another account's folder moves it there: the copy lands on the other
 * server first, and the original leaves its inbox for the Trash only afterwards. Checked against
 * both servers rather than the screen, because the screen cannot say where mail actually is.
 */
test("a message dragged to another account's folder is copied there and leaves the original inbox", async () => {
	await clearInbox(sourcePort);
	await clearInbox(targetPort);
	const subject = `Across ${Date.now()}`;
	await appendMessage(sourcePort, subject);

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, sourcePort);
		await createImapAccount(window, targetPort, undefined, {
			displayName: "Second",
			emailAddress: "second@mylomail.local",
		});

		const accountSection = (name: string) =>
			window
				.locator("section")
				.filter({ has: window.getByRole("button", { name, exact: true }) });
		await accountSection("Matrix")
			.getByRole("button", { name: /^Inbox/ })
			.first()
			.click({ timeout: 60_000 });
		const row = window.getByRole("button", { name: new RegExp(subject) });
		await expect(row).toBeVisible({ timeout: 60_000 });

		// A native drag session is not available to the test driver, so the same events a drag produces
		// are dispatched with one shared DataTransfer: the row starts it, the folder receives it.
		const target = accountSection("Second")
			.getByRole("button", { name: /^Inbox/ })
			.first();
		const transfer = await window.evaluateHandle(() => new DataTransfer());
		await row.dispatchEvent("dragstart", { dataTransfer: transfer });
		await target.dispatchEvent("dragover", { dataTransfer: transfer });
		await target.dispatchEvent("drop", { dataTransfer: transfer });

		// Gone from the list straight away, with no notice; the move carries on in the background.
		await expect(row).toBeHidden();
		await expect
			.poll(() => countWithSubject(targetPort, "INBOX", subject), {
				timeout: 90_000,
				message: "the copy never reached the other account's server",
			})
			.toBe(1);
		await expect
			.poll(() => countWithSubject(sourcePort, "INBOX", subject), {
				timeout: 90_000,
				message: "the original was never removed from its inbox",
			})
			.toBe(0);
	} finally {
		await app.close();
	}
});
