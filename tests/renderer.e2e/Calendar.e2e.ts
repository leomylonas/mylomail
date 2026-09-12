import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";

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
		await createImapAccount(window, imapPort);

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

test("new event editing exposes IANA timezone and lossless recurrence controls", async () => {
	const { app, window, dataDirectory } = await launchApp();

	try {
		await createImapAccount(window, imapPort);
		await expect(window.getByRole("button", { name: "Calendar" })).toBeEnabled({
			timeout: 60_000,
		});

		const database = join(dataDirectory, "app.db");
		const accountId = execFileSync(
			"sqlite3",
			[database, 'SELECT "Id" FROM "Accounts" LIMIT 1;'],
			{ encoding: "utf8" },
		).trim();
		const calendarId = randomUUID();
		execFileSync("sqlite3", [
			database,
			`INSERT INTO "Calendars" ("Id", "AccountId", "ProviderCalendarId", "Name", "Colour", "IsDefault", "SyncCursor", "SyncWindowStartedAt", "SyncWindowRebasing", "IsLocalOnly") VALUES ('${calendarId}', '${accountId}', 'e2e-calendar', 'Calendar', '#0f62fe', 1, NULL, NULL, 0, 1);`,
		]);

		await window.getByRole("button", { name: "Calendar" }).click();
		await window
			.getByRole("button", { name: /^Add an event on / })
			.first()
			.click();

		await expect(window.getByLabel("Start time zone")).toBeVisible();
		await window.getByLabel("Start time zone").selectOption("America/New_York");
		await window.getByLabel("Repeat").selectOption("weekly");
		await expect(window.getByLabel("Recurrence rules")).toHaveValue(
			/^FREQ=WEEKLY;BYDAY=(?:SU|MO|TU|WE|TH|FR|SA)$/u,
		);
		await expect(
			window.getByLabel("Additional occurrence dates"),
		).toBeVisible();
		await expect(window.getByLabel("Excluded occurrence dates")).toBeVisible();

		await window.getByLabel("Additional occurrence dates").fill("not-a-date");
		await expect(
			window.getByText(
				"Recurrence date 'not-a-date' must use YYYY-MM-DDTHH:mm with optional seconds and milliseconds.",
			),
		).toBeVisible();
		await expect(window.getByRole("button", { name: "Save" })).toBeDisabled();
	} finally {
		await app.close();
	}
});
