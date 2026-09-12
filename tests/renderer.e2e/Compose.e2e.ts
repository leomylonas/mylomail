import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
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
	const formattingCspViolations: string[] = [];
	window.on("console", (message) => {
		if (
			message.text().includes("Applying inline style") &&
			message.text().includes("style-src 'self'")
		)
			formattingCspViolations.push(message.text());
	});

	try {
		await createImapAccount(window, imapPort);

		await window.getByRole("button", { name: "Account settings" }).click();
		await window.getByRole("button", { name: "Edit", exact: true }).click();
		await window
			.getByLabel("Signature (HTML)")
			.fill("<p>Primary signature</p>");
		await window
			.getByRole("button", { name: "Save", exact: true })
			.first()
			.click();
		await window.getByRole("button", { name: "Add identity" }).click();
		await window.getByLabel("Display name").fill("Alias");
		await window.getByLabel("Email address").fill("alias@example.org");
		await window.getByLabel("Signature (HTML)").fill("<p>Alias signature</p>");
		await window.getByRole("button", { name: "Add", exact: true }).click();
		await expect(window.getByText(/Alias <alias@example\.org>/)).toBeVisible();
		await window.getByRole("button", { name: "Close", exact: true }).click();

		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: "New message" }).click();

		const message = window.getByLabel("Message", { exact: true });
		await expect(message).toContainText("Primary signature");
		await message.click();
		await window.keyboard.press("Control+Home");
		await window.keyboard.type("Draft text");
		await window
			.getByLabel("From")
			.selectOption({ label: "Alias <alias@example.org>" });
		await expect(message).toContainText("Draft text");
		await expect(message).toContainText("Alias signature");
		await expect(message).not.toContainText("Primary signature");
		await window
			.getByLabel("From")
			.selectOption({ label: "Matrix <test@mylomail.local>" });
		await expect(message).toContainText("Draft text");
		await expect(message).toContainText("Primary signature");
		await expect(message).not.toContainText("Alias signature");

		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill(subject);
		// The rich editor is a contenteditable, not a field: typing is the only way to
		// exercise the path that actually produces the HTML.
		await message.click();
		await window.keyboard.press("Control+End");
		await window.keyboard.type("Plain words and ");
		await window.getByRole("button", { name: "Bold" }).click();
		await window.keyboard.type("bold ones");
		await window.getByRole("button", { name: "Bold" }).click();
		await window.keyboard.type(" and ");
		await window.getByRole("button", { name: "Italic" }).click();
		await window.keyboard.type("italic ones");
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
		const deliveredResponse = await fetch(`${mailpitApi}/messages`);
		const deliveredBody = (await deliveredResponse.json()) as {
			messages: { ID: string; Subject: string }[];
		};
		const delivered = deliveredBody.messages.find(
			(message) => message.Subject === subject,
		);
		if (!delivered)
			throw new Error("The composed message disappeared from Mailpit.");
		const messageResponse = await fetch(
			`${mailpitApi}/message/${delivered.ID}`,
		);
		const received = (await messageResponse.json()) as { HTML: string };
		expect(received.HTML).toContain("<strong><span>bold ones</span></strong>");
		expect(received.HTML).toContain("<em><span>italic ones</span></em>");
		expect(formattingCspViolations).toEqual([]);
	} finally {
		await app.close();
	}
});
