import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import {
	appendHostileHtmlMessage,
	clearInbox,
} from "@mylomail/renderer-e2e/SeedImap";

/**
 * The CONDSTORE tier of the local matrix (`pnpm imap:up`).
 *
 * Each spec uses a different tier so they cannot disturb one another's mailbox — they share
 * no state, and the suite covers three capability tiers rather than one.
 */
const imapPort = 12143;

/**
 * The reading pane against a message written to attack it (§13).
 *
 * Sanitisation is unit-tested, but only the real app can show that the isolated document, its
 * content policy, the blob-URL path for inline images and the remote-content block actually
 * compose — each is individually correct in ways that could still fail together.
 */
test("hostile HTML renders safely and blocks tracking", async () => {
	await clearInbox(imapPort);
	await appendHostileHtmlMessage(imapPort, "Hostile message");

	const { app, window } = await launchApp();

	try {
		await window.evaluate(async () => {
			await fetch("/accounts", {
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
		});

		await window
			.getByRole("button", { name: /INBOX/ })
			.click({ timeout: 60_000 });
		await window
			.getByRole("button", { name: /Hostile message/ })
			.click({ timeout: 60_000 });

		// The pane names the message it is showing.
		await expect(
			window.getByRole("heading", { name: "Hostile message", level: 2 }),
		).toBeVisible({ timeout: 60_000 });

		const body = window.frameLocator('iframe[title="Message body"]');
		await expect(body.locator("#visible-body")).toHaveText(
			"Hostile body text",
			{
				timeout: 60_000,
			},
		);

		// The script never ran, and cannot have: the frame's policy forbids script entirely,
		// and sanitisation removed the element before it got there.
		expect(await window.evaluate(() => "pwned" in window)).toBe(false);
		await expect(body.locator("script")).toHaveCount(0);

		// The tracking pixel is withheld until asked for, and the user is told why.
		await expect(body.locator("#tracker")).not.toHaveAttribute(
			"src",
			/tracker/,
		);
		await expect(window.getByText(/Remote content is blocked/)).toBeVisible();

		// The renderer fetches the authenticated MIME part before passing the isolated frame a
		// blob URL; an img request cannot carry the launch credential itself.
		await expect(window.locator("[data-inline-status]")).toHaveAttribute(
			"data-inline-status",
			"resolved",
		);
		await expect(body.locator("#inline")).toHaveAttribute("src", /^blob:/);
	} finally {
		await app.close();
	}
});
