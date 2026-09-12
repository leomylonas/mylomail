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

test("calendar dates and times follow the process locale", async () => {
	const { app, window, dataDirectory } = await launchApp([], {
		LANG: "de_DE.UTF-8",
		LANGUAGE: "de_DE",
		LC_ALL: "de_DE.UTF-8",
	});

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
		const start = new Date();
		start.setHours(16, 5, 0, 0);
		const end = new Date(start.getTime() + 60 * 60 * 1000);
		sqliteValue(
			database,
			`INSERT INTO "Calendars" ("Id", "AccountId", "ProviderCalendarId", "Name", "Colour", "IsDefault", "SyncCursor", "SyncWindowStartedAt", "SyncWindowRebasing", "IsLocalOnly") VALUES ('${calendarId}', '${accountId}', 'locale-calendar', 'Calendar', '#0f62fe', 1, NULL, NULL, 0, 1); INSERT INTO "CalendarEvents" ("Id", "CalendarId", "ProviderEventId", "ICalUid", "ProviderRevision", "Sequence", "Title", "Location", "Description", "Start", "End", "StartTimeZoneId", "EndTimeZoneId", "IsAllDay", "Organizer", "Attendees", "Status", "Reminders", "RecurrenceRules", "RecurrenceDates", "ExceptionDates", "RecurrenceMasterId", "RecurrenceId", "SyncConflict") VALUES ('${eventId}', '${calendarId}', 'locale:${eventId}', 'locale-event', NULL, 0, 'Locale event', NULL, NULL, '${start.toISOString()}', '${end.toISOString()}', 'Etc/UTC', 'Etc/UTC', 0, NULL, '[]', 0, '[]', '[]', '[]', '[]', NULL, NULL, 0);`,
		);

		await window.getByRole("button", { name: "Calendar", exact: true }).click();
		const expected = await window.evaluate(() => {
			const locale = Intl.DateTimeFormat().resolvedOptions().locale;
			const info = (
				new Intl.Locale(locale) as Intl.Locale & {
					weekInfo?: { firstDay: number };
				}
			).weekInfo;
			const firstDayIndex = (info?.firstDay ?? 7) % 7;
			const sunday = new Date(2024, 0, 7);
			const weekdays = Array.from({ length: 7 }, (_, index) => {
				const date = new Date(sunday);
				date.setDate(sunday.getDate() + ((firstDayIndex + index) % 7));
				return new Intl.DateTimeFormat(undefined, { weekday: "short" }).format(
					date,
				);
			});
			return {
				locale,
				weekdays,
				month: new Intl.DateTimeFormat(undefined, {
					month: "long",
					year: "numeric",
				}).format(new Date()),
				time: new Intl.DateTimeFormat(undefined, {
					hour: "numeric",
					minute: "2-digit",
				}).format(new Date(new Date().setHours(16, 5, 0, 0))),
			};
		});
		expect(expected.locale).toMatch(/^de(?:-|$)/iu);
		await expect(window.getByRole("heading", { level: 2 })).toHaveText(
			expected.month,
		);
		await expect(window.getByRole("columnheader")).toHaveText(
			expected.weekdays,
		);

		await window.getByText("Agenda", { exact: true }).click();
		await expect(
			window.getByRole("button", { name: /Locale event/u }),
		).toContainText(expected.time);
	} finally {
		await app.close();
	}
});
