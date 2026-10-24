import { mkdtempSync, readFileSync, readdirSync, existsSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

const port = 13143;

/**
 * The wizard end to end: pick the folder, the format and a destination, start, minimise, and find
 * the mail on disk as an mbox. The native folder picker cannot be driven, so the main process's
 * dialog is answered for it.
 */
test("the export wizard writes the chosen folder as an mbox and keeps running when minimised", async () => {
	await clearInbox(port);
	const subject = `Exported ${Date.now()}`;
	await appendMessage(port, subject);
	const destination = mkdtempSync(join(tmpdir(), "mylomail-export-e2e-"));

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, port);
		await app.evaluate(({ dialog }, path) => {
			dialog.showOpenDialog = () =>
				Promise.resolve({ canceled: false, filePaths: [path] });
		}, destination);

		await window
			.getByRole("button", { name: "Inbox" })
			.first()
			.waitFor({ timeout: 60_000 });
		await window.getByRole("button", { name: "Export mail" }).click();
		const wizard = window.getByRole("dialog", { name: "Export mail" });
		await expect(wizard.getByText(/messages in \d+ folders?/)).toBeVisible({
			timeout: 60_000,
		});
		await wizard.getByRole("button", { name: "Next" }).click();

		await wizard.getByLabel(/mbox files/).check({ force: true });
		await wizard.getByRole("button", { name: "Next" }).click();
		await wizard.getByRole("button", { name: "Choose folder…" }).click();
		await expect(wizard.getByText(destination)).toBeVisible();
		await wizard.getByRole("button", { name: "Start export" }).click();

		// A single small message can finish before the progress view is drawn, so it is
		// "Minimise" while running and "Close" once done; either leaves the wizard.
		await wizard
			.getByRole("button", { name: /^(Minimise|Close)$/ })
			.first()
			.click();
		await expect(wizard).toBeHidden();

		await expect
			.poll(
				() =>
					existsSync(join(destination, "INBOX")) &&
					readFileSync(join(destination, "INBOX"), "utf8").includes(subject),
				{ timeout: 60_000, message: "the message never reached the mbox file" },
			)
			.toBe(true);
		expect(readdirSync(destination)).toContain("INBOX");

		// Reopening shows the finished export.
		await window.getByRole("button", { name: "Export mail" }).click();
		await expect(
			window
				.getByRole("dialog", { name: "Export mail" })
				.getByText("Export complete"),
		).toBeVisible({ timeout: 30_000 });
	} finally {
		await app.close();
	}
});

test("the export wizard can write the account as an Outlook PST", async () => {
	await clearInbox(port);
	await appendMessage(port, `Pst ${Date.now()}`);
	const destination = mkdtempSync(join(tmpdir(), "mylomail-export-e2e-"));

	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, port);
		await app.evaluate(({ dialog }, path) => {
			dialog.showOpenDialog = () =>
				Promise.resolve({ canceled: false, filePaths: [path] });
		}, destination);

		await window
			.getByRole("button", { name: "Inbox" })
			.first()
			.waitFor({ timeout: 60_000 });
		await window.getByRole("button", { name: "Export mail" }).click();
		const wizard = window.getByRole("dialog", { name: "Export mail" });
		await expect(wizard.getByText(/messages in \d+ folders?/)).toBeVisible({
			timeout: 60_000,
		});
		await wizard.getByRole("button", { name: "Next" }).click();
		await wizard.getByLabel(/Outlook PST file/).check({ force: true });
		await wizard.getByRole("button", { name: "Next" }).click();
		await wizard.getByRole("button", { name: "Choose folder…" }).click();
		await wizard.getByRole("button", { name: "Start export" }).click();
		await expect(wizard.getByText("Export complete")).toBeVisible({
			timeout: 60_000,
		});

		const pst = readdirSync(destination).filter((name) =>
			name.endsWith(".pst"),
		);
		expect(pst).toHaveLength(1);
		// Every Unicode PST starts with the magic "!BDN".
		expect(
			readFileSync(join(destination, pst[0]!)).subarray(0, 4).toString(),
		).toBe("!BDN");
		expect(
			readdirSync(destination).filter((n) => n.endsWith(".mylomail-export")),
		).toEqual([]);
	} finally {
		await app.close();
	}
});
