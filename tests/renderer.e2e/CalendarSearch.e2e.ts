import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";

const imapPort = 13143;

function sqliteValue(database: string, sql: string): string {
	return execFileSync("sqlite3", ["-cmd", ".timeout 10000", database, sql], {
		encoding: "utf8",
	}).trim();
}

/**
 * The search bar is on every tab and searches that tab's own thing: typing in Calendar finds the
 * event (wherever it is in time) and the mail list is not involved.
 */
test("the search bar on the Calendar tab finds events, wherever they are in time", async () => {
	const { app, window, dataDirectory } = await launchApp();
	try {
		await createImapAccount(window, imapPort);
		await expect(
			window.getByRole("button", { name: "Calendar", exact: true }),
		).toBeEnabled({ timeout: 60_000 });

		const database = join(dataDirectory, "app.db");
		const accountId = sqliteValue(
			database,
			'SELECT "Id" FROM "Accounts" LIMIT 1;',
		);
		const calendarId = randomUUID().toUpperCase();
		const eventId = randomUUID().toUpperCase();
		const start = new Date(Date.now() + 400 * 24 * 60 * 60 * 1000);
		start.setHours(16, 5, 0, 0);
		const end = new Date(start.getTime() + 60 * 60 * 1000);
		sqliteValue(
			database,
			`INSERT INTO "Calendars" ("Id", "AccountId", "ProviderCalendarId", "Name", "Colour", "IsDefault", "SyncCursor", "SyncWindowStartedAt", "SyncWindowRebasing", "IsLocalOnly") VALUES ('${calendarId}', '${accountId}', 'search-calendar', 'Calendar', '#0f62fe', 1, NULL, NULL, 0, 1); INSERT INTO "CalendarEvents" ("Id", "CalendarId", "ProviderEventId", "ICalUid", "ProviderRevision", "Sequence", "Title", "Location", "Description", "Start", "End", "StartTimeZoneId", "EndTimeZoneId", "IsAllDay", "Organizer", "Attendees", "Status", "Reminders", "RecurrenceRules", "RecurrenceDates", "ExceptionDates", "RecurrenceMasterId", "RecurrenceId", "SyncConflict") VALUES ('${eventId}', '${calendarId}', 'search:${eventId}', 'search-event', NULL, 0, 'Quarterly planning', NULL, NULL, '${start.toISOString()}', '${end.toISOString()}', 'Etc/UTC', 'Etc/UTC', 0, NULL, '[]', 0, '[]', '[]', '[]', '[]', NULL, NULL, 0);`,
		);

		await window.getByRole("button", { name: "Calendar", exact: true }).click();
		const search = window.getByRole("searchbox", { name: "Search calendar" });
		await search.fill("quarterly");
		// A year out, so it is nowhere near the month on screen.
		await expect(
			window.getByRole("button", { name: /Quarterly planning/u }),
		).toBeVisible();

		await search.fill("nothing like this");
		await expect(window.getByText(/No events match/u)).toBeVisible();
	} finally {
		await app.close();
	}
});
