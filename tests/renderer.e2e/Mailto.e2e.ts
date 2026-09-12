import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";

const imapPort = 13143;

test("a startup mailto activation opens a prefilled message", async () => {
	const uri =
		"mailto:first@example.org?to=second%40example.org&cc=copy%40example.org&bcc=blind%40example.org&subject=Mailto%20subject&body=First%20%3Cunsafe%3E%0ASecond%20line";
	const { app, window } = await launchApp([uri]);

	try {
		await createImapAccount(window, imapPort);

		await expect(window.getByLabel("To", { exact: true })).toHaveValue(
			"first@example.org, second@example.org",
			{ timeout: 60_000 },
		);
		await expect(window.getByLabel("Cc", { exact: true })).toHaveValue(
			"copy@example.org",
		);
		await expect(window.getByLabel("Bcc", { exact: true })).toHaveValue(
			"blind@example.org",
		);
		await expect(window.getByLabel("Subject")).toHaveValue("Mailto subject");
		const message = window.getByLabel("Message", { exact: true });
		await expect(message).toContainText("First <unsafe>");
		await expect(message).toContainText("Second line");
		await expect(message.locator("unsafe")).toHaveCount(0);
	} finally {
		await app.close();
	}
});
