import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
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

		const created = await window.evaluate(async () => {
			const response = await fetch("/accounts", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({
					displayName: "Matrix",
					providerType: 0,
					emailAddress: "test@mylomail.local",
					secret: "password",
					imap: {
						host: "127.0.0.1",
						port: 12143,
						useSsl: false,
						userName: "test@mylomail.local",
						smtpHost: "127.0.0.1",
						smtpPort: 1025,
					},
				}),
			});
			return response.status;
		});
		expect(created).toBe(201);

		await window
			.getByRole("button", { name: /INBOX/ })
			.first()
			.waitFor({ timeout: 60_000 });

		await window
			.getByRole("button", { name: /INBOX/ })
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
