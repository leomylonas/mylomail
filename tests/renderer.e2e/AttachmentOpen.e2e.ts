import {
	existsSync,
	mkdtempSync,
	readFileSync,
	rmSync,
	statSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendAttachmentMessage,
	clearInbox,
} from "@mylomail/renderer-e2e/SeedImap";

const imapPort = 11143;
test.skip(
	process.platform !== "linux",
	"The isolated XDG handler is Linux-specific.",
);

test("an attachment opens through the OS and its private copy is cleaned up", async () => {
	await clearInbox(imapPort);
	const proof = `attachment-open-${Date.now()}`;
	await appendAttachmentMessage(
		imapPort,
		"Attachment lifecycle",
		"proof.txt",
		proof,
	);

	const markerRoot = mkdtempSync(join(tmpdir(), "mylomail-attachment-open-"));
	const marker = join(markerRoot, "opened-path");
	const launched = await launchApp([], {
		MYLOMAIL_E2E_ATTACHMENT_OPEN_MARKER: marker,
		DBUS_SESSION_BUS_ADDRESS: "",
	});
	let closed = false;

	try {
		await createImapAccount(launched.window, imapPort);
		const inbox = launched.window.getByRole("button", { name: /INBOX/ });
		await expect(inbox).toBeVisible({ timeout: 60_000 });
		await inbox.click();
		const message = launched.window.getByRole("button", {
			name: /Attachment lifecycle/,
		});
		await expect(message).toBeVisible({ timeout: 60_000 });
		await message.click();

		const attachments = launched.window.getByRole("region", {
			name: "Attachments",
		});
		await expect(attachments.getByText(/proof\.txt/)).toBeVisible({
			timeout: 60_000,
		});
		await attachments.getByRole("button", { name: "Open" }).click();
		await expect.poll(() => existsSync(marker), { timeout: 30_000 }).toBe(true);

		const openedPath = readFileSync(marker, "utf8");
		expect(readFileSync(openedPath, "utf8")).toBe(proof);
		expect(statSync(openedPath).mode & 0o777).toBe(0o600);
		expect(dirname(dirname(openedPath))).toBe(
			join(launched.dataDirectory, "tmp", "attachments"),
		);

		await launched.app.close();
		closed = true;
		await expect
			.poll(
				() => existsSync(join(launched.dataDirectory, "tmp", "attachments")),
				{ timeout: 10_000 },
			)
			.toBe(false);
	} finally {
		if (!closed) await launched.app.close();
		rmSync(markerRoot, { recursive: true, force: true });
	}
});
