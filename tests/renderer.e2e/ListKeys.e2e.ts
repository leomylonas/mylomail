import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendMessage,
	clearInbox,
	countWithSubject,
} from "@mylomail/renderer-e2e/SeedImap";

const port = 13143;

/**
 * Ctrl+A selects every message in the list, and in Trash the Delete key removes them for good
 * without asking. Checked against the server, which is the only place that knows.
 */
test("Ctrl+A selects the whole list and Delete in Trash deletes permanently", async () => {
	await clearInbox(port);
	const run = Date.now();
	for (let i = 1; i <= 3; i++) await appendMessage(port, `Keys ${run} #${i}`);

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, port);
		await window
			.getByRole("button", { name: "INBOX" })
			.first()
			.click({ timeout: 60_000 });
		const rows = window.getByRole("button", {
			name: new RegExp(`Keys ${run}`),
		});
		await expect(rows).toHaveCount(3, { timeout: 60_000 });

		await rows.first().click();
		await window.keyboard.press("Control+a");
		await expect(
			window
				.locator('[aria-selected="true"], [aria-pressed="true"]')
				.filter({ hasText: `Keys ${run}` }),
		).toHaveCount(3);

		// To Trash first, then permanently out of it.
		await window.keyboard.press("Delete");
		await expect(rows).toHaveCount(0, { timeout: 30_000 });
		await window.getByRole("button", { name: "Trash" }).first().click();
		await expect(rows).toHaveCount(3, { timeout: 60_000 });
		await rows.first().click();
		await window.keyboard.press("Control+a");
		await window.keyboard.press("Delete");
		await expect(rows).toHaveCount(0, { timeout: 30_000 });
		await expect
			.poll(() => countWithSubject(port, "Trash", `Keys ${run}`), {
				timeout: 60_000,
			})
			.toBe(0);
	} finally {
		await app.close();
	}
});
