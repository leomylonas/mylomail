import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { clearInbox } from "@mylomail/renderer-e2e/SeedImap";

/**
 * The Basic tier of the local matrix (`pnpm imap:up`).
 *
 * Each spec uses a different tier so they cannot disturb one another's mailbox — they share
 * no state, and the suite covers three capability tiers rather than one.
 */
const imapPort = 13143;
const mailpitApi = "http://127.0.0.1:18025/api/v1";

/**
 * Compose and send, verified at the SMTP server rather than in the app.
 *
 * The outbox, its undo window and its ambiguous-outcome reconciliation are all unit-tested,
 * but none of that says a message left the machine. Mailpit is in the matrix precisely so
 * this can ask the receiving server what arrived.
 */
test("a composed message is sent and arrives at the server", async () => {
	await clearInbox(imapPort);
	await fetch(`${mailpitApi}/messages`, { method: "DELETE" });

	const { app, window } = await launchApp();
	const subject = `Composed ${Date.now()}`;

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
						port: 13143,
						useSsl: false,
						userName: "test@mylomail.local",
						smtpHost: "127.0.0.1",
						smtpPort: 11025,
					},
				}),
			});
		});

		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: "New message" }).click();

		await window.getByLabel("To").fill("someone@example.org");
		await window.getByLabel("Subject").fill(subject);
		await window.getByLabel("Message").fill("Sent from a test.");
		await window.getByRole("button", { name: "Send", exact: true }).click();

		await expect(window.getByText("Sending…")).toBeVisible();

		// The receiving server is the only witness that matters: everything before this point
		// is the app agreeing with itself.
		await expect
			.poll(
				async () => {
					const response = await fetch(`${mailpitApi}/messages`);
					const body = (await response.json()) as {
						messages: { Subject: string }[];
					};
					return body.messages.filter((m) => m.Subject === subject).length;
				},
				{
					timeout: 90_000,
					message: "the message never reached the SMTP server",
				},
			)
			.toBe(1);
	} finally {
		await app.close();
	}
});
