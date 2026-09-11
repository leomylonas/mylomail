import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";

/**
 * The CondStore tier of the local matrix (`pnpm imap:up`), shared with `Mailboxes.e2e.ts`
 * but never touched by it: this spec only ever adds an account, it never creates or renames
 * folders, so the two cannot disturb each other.
 */
const imapPort = 12143;

/**
 * A fresh launch has no accounts, so the app has to get the user from nothing to a usable
 * mailbox through its own UI — the one thing every other spec in this suite has always done
 * with a raw `fetch("/accounts", ...)` instead.
 */
test("a new user adds an IMAP account through the form and reaches their inbox", async () => {
	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({
			timeout: 30_000,
		});

		await window.getByLabel("Account name").fill("Matrix");
		await window.getByLabel("Email address").fill("test@mylomail.local");
		await window.getByLabel("IMAP host").fill("127.0.0.1");
		await window.getByLabel("Port", { exact: true }).fill(String(imapPort));
		// Carbon's Toggle keeps its actual <button> visually hidden for accessibility; the
		// clickable surface a user sees is the <label>, which forwards the click natively.
		await window.locator('label[for="add-account-ssl"]').click();
		await window.getByLabel("Password", { exact: true }).fill("password");
		await window.getByLabel("SMTP host").fill("127.0.0.1");
		await window.getByLabel("SMTP port").fill("11025");

		await window.getByRole("button", { name: "Create account" }).click();

		// The form is gone and the mailbox tree it unblocked is visible: the account round
		// tripped through the real endpoint rather than the mutation merely resolving locally.
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeHidden({ timeout: 30_000 });
		await expect(window.getByRole("button", { name: /INBOX/ })).toBeVisible({
			timeout: 60_000,
		});
	} finally {
		await app.close();
	}
});

test("an IMAP account can include independent CalDAV configuration", async () => {
	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({
			timeout: 30_000,
		});

		await window.getByLabel("Account name").fill("Self hosted");
		await window.getByLabel("Email address").fill("ada@example.test");
		await window.getByLabel("IMAP host").fill("imap.example.test");
		await window.getByLabel("Password", { exact: true }).fill("mail-secret");
		await window.getByLabel("SMTP host").fill("smtp.example.test");

		const create = window.getByRole("button", { name: "Create account" });
		await expect(create).toBeEnabled();
		await window.locator('label[for="add-account-caldav"]').click();
		await expect(create).toBeDisabled();
		await expect(window.getByLabel("CalDAV endpoint")).toBeVisible();
		await window
			.getByLabel("CalDAV endpoint")
			.fill("https://dav.example.test/calendars");
		await expect(create).toBeEnabled();

		await window.locator('label[for="add-account-caldav-reuse"]').click();
		await expect(create).toBeDisabled();
		await window.getByLabel("CalDAV password").fill("calendar-secret");
		await expect(create).toBeEnabled();
	} finally {
		await app.close();
	}
});

test("Google account setup is selectable and ready for interactive sign-in", async () => {
	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({
			timeout: 30_000,
		});

		await window.locator('label[for="provider-gmail"]').click();
		await window.getByLabel("Account name").fill("Personal Gmail");
		await window.getByLabel("Email address").fill("ada@example.test");

		await expect(
			window.getByText(
				"A browser window will open for secure provider sign-in.",
			),
		).toBeVisible();
		await expect(
			window.getByRole("button", { name: "Create account" }),
		).toBeEnabled();
	} finally {
		await app.close();
	}
});

test("Google account setup accepts an account-owned OAuth registration", async () => {
	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({
			timeout: 30_000,
		});

		await window.locator('label[for="provider-gmail"]').click();
		await window.getByLabel("Account name").fill("Personal Gmail");
		await window.getByLabel("Email address").fill("ada@example.test");
		await window.locator('label[for="add-account-google-byoc"]').click();

		const create = window.getByRole("button", { name: "Create account" });
		await expect(create).toBeDisabled();
		await expect(
			window.getByText(/Create a Desktop app OAuth client/),
		).toBeVisible();
		await expect(
			window.getByText(/refresh tokens that expire after about seven days/),
		).toBeVisible();

		await window.getByLabel("Google OAuth client ID").fill("client-id");
		await window.getByLabel("Google OAuth client secret").fill("client-secret");
		await expect(create).toBeEnabled();
	} finally {
		await app.close();
	}
});

test("Microsoft 365 setup is selectable and ready for interactive sign-in", async () => {
	const { app, window } = await launchApp();

	try {
		await expect(
			window.getByRole("heading", { name: "Add account" }),
		).toBeVisible({
			timeout: 30_000,
		});

		await window.locator('label[for="provider-microsoft365"]').click();
		await window.getByLabel("Account name").fill("Work");
		await window.getByLabel("Email address").fill("ada@example.test");

		await expect(
			window.getByText(
				"A browser window will open for secure provider sign-in.",
			),
		).toBeVisible();
		await expect(
			window.getByRole("button", { name: "Create account" }),
		).toBeEnabled();
	} finally {
		await app.close();
	}
});
