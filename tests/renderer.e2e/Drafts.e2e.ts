import { execFileSync } from "node:child_process";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	bodiesIn,
	clearFolder,
	countWithSubject,
} from "@mylomail/renderer-e2e/SeedImap";

function sqliteValue(database: string, sql: string): string {
	return execFileSync("sqlite3", ["-cmd", ".timeout 10000", database, sql], {
		encoding: "utf8",
	}).trim();
}

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

test("a detached compose saves before tray hide and again after restore", async () => {
	const { app, window, dataDirectory } = await launchApp();
	const marker = `Close barrier ${Date.now()}`;
	const restoredMarker = `Restored close barrier ${Date.now()}`;

	try {
		await createImapAccount(window, imapPort);
		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({ timeout: 60_000 });
		await window.getByRole("button", { name: "Settings", exact: true }).click();
		const trayOption = window.getByRole("radio", {
			name: "Keep running in the system tray",
		});
		await window
			.getByText("Keep running in the system tray", { exact: true })
			.click();
		await expect(trayOption).toBeChecked();

		await window.getByRole("button", { name: "New message" }).click();
		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill("Detached close barrier");
		await window
			.getByLabel("Message", { exact: true })
			.fill("Initial detached draft.");
		await window.getByRole("button", { name: "Open in new window" }).click();
		await expect.poll(() => app.windows().length, { timeout: 30_000 }).toBe(2);
		const detached = app
			.windows()
			.find((candidate) => candidate.url().includes("compose="));
		if (!detached) throw new Error("The detached compose window did not open.");
		await expect(detached.getByLabel("Message", { exact: true })).toBeVisible();
		await detached.getByLabel("Message", { exact: true }).fill(marker);

		await app.evaluate(({ BrowserWindow }) => {
			const main = BrowserWindow.getAllWindows().find(
				(candidate) => !candidate.webContents.getURL().includes("compose="),
			);
			if (!main) throw new Error("The main window is missing.");
			main.close();
		});
		await expect
			.poll(
				() =>
					app.evaluate(
						({ BrowserWindow }) => BrowserWindow.getAllWindows().length,
					),
				{ timeout: 10_000 },
			)
			.toBe(1);

		await app.evaluate(({ BrowserWindow }) => {
			const compose = BrowserWindow.getAllWindows().find((candidate) =>
				candidate.webContents.getURL().includes("compose="),
			);
			if (!compose) throw new Error("The compose window is missing.");
			compose.close();
		});
		await expect
			.poll(
				() =>
					app.evaluate(({ BrowserWindow }) => {
						const [remaining] = BrowserWindow.getAllWindows();
						return remaining?.isVisible() ?? true;
					}),
				{ timeout: 10_000 },
			)
			.toBe(false);
		await expect
			.poll(
				() =>
					sqliteValue(
						join(dataDirectory, "app.db"),
						`SELECT COUNT(*) FROM "Drafts" WHERE "BodyHtml" LIKE '%${marker}%';`,
					),
				{ timeout: 10_000 },
			)
			.toBe("1");

		// The approved close was intercepted by tray hiding rather than consumed by window
		// destruction. Restoring it must require a fresh save barrier on its next close.
		await app.evaluate(({ BrowserWindow }) => {
			const [compose] = BrowserWindow.getAllWindows();
			compose?.show();
		});
		await expect(detached.getByLabel("Message", { exact: true })).toBeVisible();
		await detached.getByLabel("Message", { exact: true }).fill(restoredMarker);
		await detached.evaluate(() => globalThis.window.windows?.open());
		await expect
			.poll(
				() =>
					app.evaluate(
						({ BrowserWindow }) => BrowserWindow.getAllWindows().length,
					),
				{ timeout: 10_000 },
			)
			.toBe(2);
		await app.evaluate(({ BrowserWindow }) => {
			const compose = BrowserWindow.getAllWindows().find((candidate) =>
				candidate.webContents.getURL().includes("compose="),
			);
			if (!compose) throw new Error("The restored compose window is missing.");
			compose.close();
		});
		await expect
			.poll(
				() =>
					app.evaluate(
						({ BrowserWindow }) => BrowserWindow.getAllWindows().length,
					),
				{ timeout: 10_000 },
			)
			.toBe(1);
		await expect
			.poll(
				() =>
					sqliteValue(
						join(dataDirectory, "app.db"),
						`SELECT COUNT(*) FROM "Drafts" WHERE "BodyHtml" LIKE '%${restoredMarker}%';`,
					),
				{ timeout: 10_000 },
			)
			.toBe("1");
	} finally {
		await app.close();
	}
});
