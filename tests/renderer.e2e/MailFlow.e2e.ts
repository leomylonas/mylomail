import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import {
	appendMessage,
	clearInbox,
	inboxFlags,
} from "@mylomail/renderer-e2e/SeedImap";

/** The QRESYNC tier of the local matrix (`pnpm imap:up`). */
const imapPort = 11143;

/**
 * The whole product, driven the way a user drives it.
 *
 * Everything below the UI is covered by unit and fault-injection tests, and all of it passed
 * while a mutation enqueued from the hub never reached the server — the database looked
 * right the entire time. This test is the one that would have caught that, because it asks
 * the IMAP server what happened rather than asking the app what it believes.
 */
test("a real account syncs, lists mail, and its flag changes reach the server", async () => {
	await clearInbox(imapPort);
	for (const subject of ["First message", "Second message"]) {
		await appendMessage(imapPort, subject);
	}

	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "MyloMail" }),
		).toBeVisible();

		// Adding the account through the same API the settings UI will use.
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
						port: 11143,
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

		// Topology discovery, then coverage: the sidebar fills in as they land.
		await expect(window.getByRole("button", { name: /INBOX/ })).toBeVisible({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: /INBOX/ }).click();

		const firstMessage = window.getByRole("button", { name: /First message/ });
		await expect(firstMessage).toBeVisible({ timeout: 60_000 });
		await expect(
			window.getByRole("button", { name: /Second message/ }),
		).toBeVisible();

		// Nothing is read on the server yet.
		expect((await inboxFlags(imapPort)).join(" ")).not.toContain("\\Seen");

		await firstMessage.click();

		// The mutation is enqueued locally, executed by a job, and applied by the provider.
		// Asking the server is the point: the local database would look correct either way.
		await expect
			.poll(
				async () =>
					(await inboxFlags(imapPort)).filter((f) => f.includes("\\Seen"))
						.length,
				{
					timeout: 60_000,
					message: "the flag never reached the IMAP server",
				},
			)
			.toBe(1);
	} finally {
		await app.close();
	}
});
