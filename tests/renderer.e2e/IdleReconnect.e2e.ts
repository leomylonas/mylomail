import { execFileSync } from "node:child_process";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

const imapPort = 11143;
function disconnectImapSessions() {
	execFileSync(
		"docker",
		["exec", "mylomail-imap-qresync", "doveadm", "kick", "test@mylomail.local"],
		{ stdio: "inherit" },
	);
}

test("IMAP IDLE reconnects after the server drops its live session", async () => {
	await clearInbox(imapPort);
	await appendMessage(imapPort, "Before IDLE reset");
	const launched = await launchApp();

	try {
		await createImapAccount(launched.window, imapPort);
		const inbox = launched.window.getByRole("button", { name: /INBOX/ });
		await expect(inbox).toBeVisible({ timeout: 60_000 });
		await inbox.click();
		await expect(
			launched.window.getByRole("button", { name: /Before IDLE reset/ }),
		).toBeVisible({ timeout: 60_000 });

		// Drop the authenticated socket from the server side without restarting Dovecot or
		// changing UIDVALIDITY. The worker must retire the failed session and establish a new
		// IDLE connection; no polling job is scheduled by this server-side disconnect.
		await launched.window.waitForTimeout(6_000);
		disconnectImapSessions();
		await launched.window.waitForTimeout(6_000);
		await appendMessage(imapPort, "After IDLE reconnect");
		await expect(
			launched.window.getByRole("button", { name: /After IDLE reconnect/ }),
		).toBeVisible({ timeout: 30_000 });
	} finally {
		await launched.app.close();
	}
});
