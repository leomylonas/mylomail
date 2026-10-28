import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendMessage,
	clearInbox,
	countWithSubject,
} from "@mylomail/renderer-e2e/SeedImap";

const port = 13143;

/** Ctrl+Z puts back the last delete, on the server as well as on screen. */
test("Ctrl+Z undoes a delete", async () => {
	await clearInbox(port);
	const subject = `Undo ${Date.now()}`;
	await appendMessage(port, subject);

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, port);
		await window
			.getByRole("button", { name: "INBOX" })
			.first()
			.click({ timeout: 60_000 });
		const row = window.getByRole("button", { name: new RegExp(subject) });
		await expect(row).toBeVisible({ timeout: 60_000 });

		await row.click();
		await window.keyboard.press("Delete");
		await expect(row).toBeHidden({ timeout: 30_000 });
		await expect
			.poll(() => countWithSubject(port, "INBOX", subject), { timeout: 60_000 })
			.toBe(0);

		await window.keyboard.press("Control+z");
		await expect(window.getByText("Undone").first()).toBeVisible({
			timeout: 30_000,
		});
		await expect
			.poll(() => countWithSubject(port, "INBOX", subject), {
				timeout: 60_000,
				message: "the message never came back to the inbox",
			})
			.toBe(1);
		await expect(row).toBeVisible({ timeout: 60_000 });

		// An undone delete is meant to come back, so it is not reported as one that did by mistake.
		await window.getByRole("button", { name: "Settings", exact: true }).click();
		await expect(
			window.getByText("No deleted or moved message has reappeared"),
		).toBeVisible();
	} finally {
		await app.close();
	}
});
