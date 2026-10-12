import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { foldersOn } from "@mylomail/renderer-e2e/SeedImap";

/**
 * The CONDSTORE tier, whose "." delimiter and INBOX. prefix mean a folder created here is
 * named by the server, not by the client. Creating one and reading it back is the only way
 * that shows.
 */
const imapPort = 12143;

/**
 * Folder lifecycle from the sidebar, checked against the server rather than the sidebar.
 *
 * The previous implementation used `window.prompt`, which is not merely discouraged in
 * Electron but throws — so create and rename did nothing at all in the packaged app while
 * working in a browser. Nothing in the unit suite could see that.
 */
test("a folder is created, renamed and deleted on the server", async () => {
	const { app, window } = await launchApp();
	const name = `Folder ${Date.now()}`;
	const renamed = `${name} renamed`;

	try {
		await expect(
			window.getByRole("heading", { name: "MyloMail" }),
		).toBeVisible();

		await createImapAccount(window, imapPort);

		await window
			.getByRole("button", { name: /inbox/i })
			.first()
			.waitFor({ timeout: 60_000 });

		await window
			.getByRole("button", { name: /inbox/i })
			.first()
			.click({ button: "right" });
		await window.getByRole("menuitem", { name: "New folder" }).click();
		await window.getByLabel("Folder name").fill(name);
		await window.getByRole("button", { name: "Create" }).click();

		await expect
			.poll(
				async () => (await foldersOn(imapPort)).some((f) => f.endsWith(name)),
				{
					timeout: 30_000,
					message: "the folder never reached the server",
				},
			)
			.toBe(true);

		await window
			.getByRole("button", { name: new RegExp(name) })
			.click({ button: "right" });
		await window.getByRole("menuitem", { name: "Rename" }).click();
		await window.getByLabel("Folder name").fill(renamed);
		await window.getByRole("button", { name: "Rename" }).click();

		await expect
			.poll(
				async () => {
					const folders = await foldersOn(imapPort);
					return (
						folders.some((f) => f.endsWith(renamed)) &&
						!folders.some((f) => f.endsWith(name))
					);
				},
				{ timeout: 30_000, message: "the rename never reached the server" },
			)
			.toBe(true);

		await window
			.getByRole("button", { name: new RegExp(renamed) })
			.click({ button: "right" });
		await window.getByRole("menuitem", { name: "Delete" }).click();

		// The confirmation states what this provider actually does. On IMAP the messages go
		// with the folder; on Gmail they would not, and the same wording would be a lie.
		await expect(
			window.getByText(/messages in this folder will be deleted/i),
		).toBeVisible();
		await window.getByRole("button", { name: "Delete" }).last().click();

		await expect
			.poll(
				async () =>
					(await foldersOn(imapPort)).some((f) => f.endsWith(renamed)),
				{
					timeout: 30_000,
					message: "the folder was never deleted on the server",
				},
			)
			.toBe(false);
	} finally {
		await app.close();
	}
});

test("several folders are marked together, hidden together, and shown again from the account menu", async () => {
	const { app, window } = await launchApp();

	try {
		await createImapAccount(window, imapPort);
		const folders = window.locator("[data-mailbox-id]");
		await expect(folders.first()).toBeVisible({ timeout: 60_000 });
		await expect.poll(() => folders.count()).toBeGreaterThan(2);
		const before = await folders.count();

		await folders.nth(1).click({ modifiers: ["ControlOrMeta"] });
		await folders.nth(2).click({ modifiers: ["ControlOrMeta"] });
		await folders.nth(2).click({ button: "right" });
		// Only offered for a selection of several folders.
		await window.getByRole("menuitem", { name: "Hide folders" }).click();
		await expect.poll(() => folders.count()).toBeLessThanOrEqual(before - 2);

		await window
			.locator("section > button[draggable]")
			.first()
			.click({ button: "right" });
		await window.getByRole("menuitem", { name: "Show hidden folders" }).click();
		await expect.poll(() => folders.count()).toBe(before);
	} finally {
		await app.close();
	}
});
