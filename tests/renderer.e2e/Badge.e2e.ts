import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

const port = 13143;

/** The count of unread inbox mail reaches the app's icon in the dock or taskbar. */
test("the launcher badge follows the unread count", async () => {
	await clearInbox(port);
	const run = Date.now();
	for (let i = 1; i <= 3; i++) await appendMessage(port, `Badge ${run} #${i}`);

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, port);
		await expect
			.poll(
				() => app.evaluate(({ app: electron }) => electron.getBadgeCount()),
				{
					timeout: 60_000,
					message: "the badge never showed the three unread messages",
				},
			)
			.toBe(3);

		await window.getByRole("button", { name: "INBOX" }).first().click();
		await window.getByText(`Badge ${run}`).first().click();
		await expect
			.poll(
				() => app.evaluate(({ app: electron }) => electron.getBadgeCount()),
				{
					timeout: 30_000,
				},
			)
			.toBe(2);
	} finally {
		await app.close();
	}
});
