import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";

/**
 * The Basic tier of the local matrix, shared with `Compose.e2e.ts` but never touched by it:
 * this spec never sends or reads mail, only navigates to the calendar panel.
 */
const imapPort = 13143;

/**
 * No CalDAV server exists in the local matrix, so this cannot exercise real event CRUD —
 * only that the panel itself renders, navigates, and shows a deliberate empty state rather
 * than a blank or broken-looking one, per the standing convention every collection view
 * follows (§13).
 */
test("the calendar panel renders with a deliberate empty state when no calendar is configured", async () => {
	const { app, window } = await launchApp();

	try {
		await window.evaluate(async (port) => {
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
						port,
						useSsl: false,
						userName: "test@mylomail.local",
						smtpHost: "127.0.0.1",
						smtpPort: 11025,
					},
				}),
			});
		}, imapPort);

		await expect(window.getByRole("button", { name: "Calendar" })).toBeEnabled({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: "Calendar" }).click();

		await expect(
			window.getByText("No calendars are configured on any account yet."),
		).toBeVisible();

		// The month/agenda switcher is still there and usable even with nothing to show.
		// Carbon's ContentSwitcher renders its options with role="tab", not "button".
		await window.getByRole("tab", { name: "Agenda" }).click();
		await expect(
			window.getByText("No calendars are configured on any account yet."),
		).toBeVisible();
	} finally {
		await app.close();
	}
});
