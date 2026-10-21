import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import { appendMessage, clearInbox } from "@mylomail/renderer-e2e/SeedImap";

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

		await window.getByRole("button", { name: "Settings", exact: true }).click();
		await window
			.getByRole("dialog")
			.getByRole("button", { name: "Matrix" })
			.click();
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
		await window.getByRole("button", { name: "Close settings" }).click();

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

test("the From menu moves a message to another account without losing what was typed", async () => {
	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await createImapAccount(window, 12143, undefined, {
			displayName: "Second",
			emailAddress: "second@mylomail.local",
		});
		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({ timeout: 60_000 });
		await window.getByRole("button", { name: "New message" }).click();

		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill("Moving house");
		const from = window.getByLabel("From");
		await expect(from.locator("option")).toHaveCount(2);

		await from.selectOption({ label: "Second <second@mylomail.local>" });

		await expect(from.locator("option:checked")).toHaveText(
			"Second <second@mylomail.local>",
		);
		await expect(window.getByLabel("To", { exact: true })).toHaveValue(
			"someone@example.org",
		);
		await expect(window.getByLabel("Subject")).toHaveValue("Moving house");
	} finally {
		await app.close();
	}
});

test("writing starts at the top of the message, above the signature", async () => {
	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await window.getByRole("button", { name: "Settings", exact: true }).click();
		await window
			.getByRole("dialog")
			.getByRole("button", { name: "Matrix" })
			.click();
		await window.getByRole("button", { name: "Edit", exact: true }).click();
		await window.getByLabel("Signature (HTML)").fill("<p>Bottom signature</p>");
		await window
			.getByRole("button", { name: "Save", exact: true })
			.first()
			.click();
		await window.getByRole("button", { name: "Close settings" }).click();
		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({ timeout: 60_000 });
		await window.getByRole("button", { name: "New message" }).click();

		const message = window.getByLabel("Message", { exact: true });
		await expect(message).toContainText("Bottom signature");
		// No click into the body: the caret is where a new message should start.
		await window.keyboard.type("Typed first");

		await expect(message).toHaveText(/^Typed first[\s\S]*Bottom signature/);
	} finally {
		await app.close();
	}
});

test("choosing another message while writing keeps the draft and shows that message", async () => {
	await clearInbox(imapPort);
	const subject = `Switch ${Date.now()}`;
	// The matrix server keeps its Drafts across runs, and a fresh app imports every one of them.
	const draftSubject = `Kept ${Date.now()}`;
	await appendMessage(imapPort, subject);
	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await window
			.getByRole("button", { name: /inbox/i })
			.first()
			.click({ timeout: 60_000 });
		await expect(
			window.getByRole("button", { name: new RegExp(subject) }),
		).toBeVisible({ timeout: 60_000 });

		await window.getByRole("button", { name: "New message" }).click();
		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill(draftSubject);

		await window.getByRole("button", { name: new RegExp(subject) }).click();

		await expect(window.getByLabel("To", { exact: true })).toHaveCount(0);
		await expect(window.getByRole("heading", { name: subject })).toBeVisible();
		await window
			.getByRole("button", { name: /^Drafts/ })
			.first()
			.click();
		await expect(
			window.getByRole("button", { name: new RegExp(draftSubject) }),
		).toBeVisible({ timeout: 30_000 });
	} finally {
		await app.close();
	}
});

test("opening a draft does not save it again, and a sent draft leaves the list", async () => {
	await clearInbox(imapPort);
	const draftSubject = `Reopened ${Date.now()}`;
	const { app, window } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await window.getByRole("button", { name: "New message" }).click();
		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill(draftSubject);
		await expect(window.getByText(/Draft saved at/)).toBeVisible({
			timeout: 15_000,
		});

		await window
			.getByRole("button", { name: /^Drafts/ })
			.first()
			.click();
		const row = window.getByRole("button", {
			name: new RegExp(draftSubject),
		});
		await expect(row).toContainText("someone@example.org");
		await row.click();
		await expect(window.getByLabel("Subject")).toHaveValue(draftSubject);
		// The autosave debounce is two seconds; an unedited draft must outlast it unsaved.
		await window.waitForTimeout(4_000);
		await expect(window.getByText(/Draft saved at/)).toHaveCount(0);

		await window.getByRole("button", { name: "Send", exact: true }).click();
		await expect(row).toHaveCount(0, { timeout: 60_000 });
		// And nothing puts it back once the send has been queued.
		await window.waitForTimeout(4_000);
		await expect(row).toHaveCount(0);
	} finally {
		await app.close();
	}
});

test("the sender's name, a plain-text signature and the full formatting set reach the recipient", async () => {
	await clearInbox(imapPort);
	await fetch(`${mailpitApi}/messages`, { method: "DELETE" });
	const subject = `Formatted ${Date.now()}`;
	const { app, window } = await launchApp();
	const violations: string[] = [];
	window.on("console", (message) => {
		if (message.text().includes("Content Security Policy"))
			violations.push(message.text());
	});
	try {
		await createImapAccount(window, imapPort);
		await window.getByRole("button", { name: "Settings", exact: true }).click();
		await window
			.getByRole("dialog")
			.getByRole("button", { name: "Matrix" })
			.click();
		await window.getByRole("button", { name: "Edit", exact: true }).click();
		await window.getByLabel("Display name").fill("Leo Mylonas");
		// Typed as text, the way the settings box takes it: the break between the lines is real.
		await window.getByLabel("Signature (HTML)").fill("Cheers,\nLeo");
		await window
			.getByRole("button", { name: "Save", exact: true })
			.first()
			.click();
		await window.getByRole("button", { name: "Close settings" }).click();

		await expect(
			window.getByRole("button", { name: "New message" }),
		).toBeEnabled({ timeout: 60_000 });
		await window.getByRole("button", { name: "New message" }).click();
		const message = window.getByLabel("Message", { exact: true });
		await expect(message).toContainText("Cheers,");
		await expect(message.locator("[data-mylomail-signature] br")).toHaveCount(
			1,
		);

		// A new line can be made between the two signature lines.
		await message.getByText("Cheers,").click();
		await message.evaluate(() => {
			const text = document.querySelector(
				"[data-mylomail-signature] span",
			)?.firstChild;
			if (!text) throw new Error("no signature text");
			document.getSelection()?.setBaseAndExtent(text, 7, text, 7);
		});
		await window.keyboard.press("Enter");
		await window.keyboard.type("(and thanks)");
		await expect(message.locator("[data-mylomail-signature] br")).toHaveCount(
			2,
		);

		await window.getByLabel("To", { exact: true }).fill("someone@example.org");
		await window.getByLabel("Subject").fill(subject);
		await message.evaluate(() => {
			const first = document.querySelector('[aria-label="Message"] p');
			if (!first) throw new Error("no first paragraph");
			document.getSelection()?.setBaseAndExtent(first, 0, first, 0);
		});
		await window.getByLabel("Paragraph style").selectOption("h1");
		await window.keyboard.type("Big heading");
		await window.getByRole("button", { name: "Align centre" }).click();
		await window.keyboard.press("Enter");
		await window.getByLabel("Paragraph style").selectOption("paragraph");
		await window.getByLabel("Text colour").fill("#ff0000");
		await window.keyboard.type("red words");
		await window.getByRole("button", { name: "Underline" }).click();
		await window.keyboard.type(" and underlined");
		await window.getByRole("button", { name: "Send", exact: true }).click();

		await expect
			.poll(
				async () => {
					const response = await fetch(`${mailpitApi}/messages`);
					const body = (await response.json()) as {
						messages: { ID: string; Subject: string }[];
					};
					return body.messages.filter((m) => m.Subject === subject).length;
				},
				{
					timeout: 90_000,
					message: "the message never reached the SMTP server",
				},
			)
			.toBe(1);
		const list = (await (await fetch(`${mailpitApi}/messages`)).json()) as {
			messages: { ID: string; Subject: string }[];
		};
		const id = list.messages.find((m) => m.Subject === subject)!.ID;
		const received = (await (
			await fetch(`${mailpitApi}/message/${id}`)
		).json()) as {
			HTML: string;
			From: { Name: string };
		};
		expect(received.From.Name).toBe("Leo Mylonas");
		expect(received.HTML).toContain("<h1");
		expect(received.HTML).toMatch(/text-align:\s*center/);
		expect(received.HTML).toMatch(/color:\s*(#ff0000|rgb\(255, 0, 0\))/);
		expect(received.HTML).toContain("<u>");

		expect(received.HTML).toContain(
			"Cheers,</span><br><span>(and thanks)</span><br><span>Leo",
		);
		expect(violations).toEqual([]);
	} finally {
		await app.close();
	}
});
