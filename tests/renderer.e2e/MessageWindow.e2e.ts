import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

const imapPort = 13143;

test("a message opened in its own window offers delete and the rest of the message actions", async () => {
	await clearInbox(imapPort);
	const subject = `Own window ${Date.now()}`;
	await appendMessage(imapPort, subject);
	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await window
			.getByRole("button", { name: /inbox/i })
			.first()
			.click({ timeout: 60_000 });
		await window
			.getByRole("button", { name: new RegExp(subject) })
			.dblclick({ timeout: 60_000 });

		await expect.poll(() => app.windows().length, { timeout: 30_000 }).toBe(2);
		const own = app.windows().find((candidate) => candidate !== window)!;
		// The same commands, in the same order, as the main window's toolbar.
		const toolbar = own.getByRole("toolbar", { name: "Message actions" });
		await expect(
			toolbar.getByRole("button", { name: "Delete", exact: true }),
		).toBeVisible({ timeout: 30_000 });
		const order = [
			"Delete",
			"Archive",
			"Move to",
			"Report junk",
			"Reply",
			"Reply all",
			"Forward",
			"Mark unread",
			"Flag",
			"Print",
		];
		const names = await toolbar
			.getByRole("button")
			.evaluateAll((buttons) =>
				buttons.map((button) => button.textContent?.trim() ?? ""),
			);
		expect(names.filter((name) => order.includes(name))).toEqual(order);

		await toolbar.getByRole("button", { name: "More message actions" }).click();
		for (const item of ["Delete permanently", "Save as .eml"]) {
			await expect(own.getByRole("menuitem", { name: item })).toBeVisible();
		}
		await own.keyboard.press("Escape");
		await own.getByRole("button", { name: "Message options" }).click();
		for (const item of ["View source", "Print"]) {
			await expect(own.getByRole("menuitem", { name: item })).toBeVisible();
		}
	} finally {
		await app.close();
	}
});
