import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { request } from "node:https";
import { join } from "node:path";
import { expect, test } from "@playwright/test";
import { launchApp } from "@mylomail/renderer-e2e/AppFixture";
import { createImapAccount } from "@mylomail/renderer-e2e/SeedAccount";
import {
	appendCalendarMessage,
	clearInbox,
} from "@mylomail/renderer-e2e/SeedImap";

/**
 * The Basic tier of the local matrix, shared with `Compose.e2e.ts` but never touched by it:
 * this spec never sends or reads mail, only navigates to the calendar panel.
 */
const imapPort = 13143;
const inviteImapPort = 11143;
const calDavBase = "https://127.0.0.1:15232/";
const calDavUser = "test@mylomail.local";
const calDavPassword = "password";
const mailpitApi = "http://127.0.0.1:18025/api/v1";

async function calDavRequest(
	method: string,
	target: string,
): Promise<{ status: number; body: string }> {
	return await new Promise((resolve, reject) => {
		const req = request(
			target,
			{
				method,
				rejectUnauthorized: false,
				headers: {
					Authorization: `Basic ${Buffer.from(`${calDavUser}:${calDavPassword}`).toString("base64")}`,
				},
			},
			(response) => {
				const chunks: Buffer[] = [];
				response.on("data", (chunk: Buffer) => chunks.push(chunk));
				response.on("end", () =>
					resolve({
						status: response.statusCode ?? 0,
						body: Buffer.concat(chunks).toString("utf8"),
					}),
				);
			},
		);
		req.on("error", reject);
		req.end();
	});
}

function executeSqlite(database: string, sql: string): string {
	return execFileSync("sqlite3", ["-cmd", ".timeout 10000", database, sql], {
		encoding: "utf8",
	}).trim();
}

function sqliteValue(database: string, sql: string): string {
	try {
		return executeSqlite(database, sql);
	} catch {
		return "";
	}
}

function icsDate(value: Date): string {
	return value
		.toISOString()
		.replaceAll("-", "")
		.replaceAll(":", "")
		.replace(/\.\d{3}Z$/u, "Z");
}

function insertLocalInviteEvent(
	database: string,
	calendarId: string,
	event: {
		id: string;
		uid: string;
		title: string;
		start: Date;
		end: Date;
		organizerName: string;
		organizerEmail: string;
		attendeeName: string;
		attendeeEmail: string;
	},
): void {
	const organizer = JSON.stringify({
		Name: event.organizerName,
		Email: event.organizerEmail,
	});
	const attendees = JSON.stringify([
		{
			Name: event.attendeeName,
			Email: event.attendeeEmail,
			Role: 0,
			ResponseStatus: 0,
		},
	]);
	const eventId = event.id.toUpperCase();
	const storedCalendarId = calendarId.toUpperCase();
	executeSqlite(
		database,
		`INSERT INTO "CalendarEvents" ("Id", "CalendarId", "ProviderEventId", "ICalUid", "ProviderRevision", "Sequence", "Title", "Location", "Description", "Start", "End", "StartTimeZoneId", "EndTimeZoneId", "IsAllDay", "Organizer", "Attendees", "Status", "Reminders", "RecurrenceRules", "RecurrenceDates", "ExceptionDates", "RecurrenceMasterId", "RecurrenceId", "SyncConflict") VALUES ('${eventId}', '${storedCalendarId}', 'mail:${eventId}', '${event.uid}', NULL, 0, '${event.title}', NULL, NULL, '${event.start.toISOString()}', '${event.end.toISOString()}', 'Etc/UTC', 'Etc/UTC', 0, '${organizer}', '${attendees}', 0, '[]', '[]', '[]', '[]', NULL, NULL, 0);`,
	);
}

/**
 * An account without a calendar still gets the deliberate empty-state surface rather than a
 * blank or broken-looking panel (§13).
 */
test("the calendar panel renders with a deliberate empty state when no calendar is configured", async () => {
	const { app, window } = await launchApp();

	try {
		await createImapAccount(window, imapPort);

		await expect(
			window.getByRole("button", { name: "Calendar", exact: true }),
		).toBeEnabled({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: "Calendar", exact: true }).click();

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
		await expect(
			window.getByRole("button", { name: "Calendar", exact: true }),
		).toBeEnabled({
			timeout: 60_000,
		});

		const database = join(dataDirectory, "app.db");
		const accountId = sqliteValue(
			database,
			'SELECT "Id" FROM "Accounts" LIMIT 1;',
		);
		const calendarId = randomUUID().toUpperCase();
		executeSqlite(
			database,
			`INSERT INTO "Calendars" ("Id", "AccountId", "ProviderCalendarId", "Name", "Colour", "IsDefault", "SyncCursor", "SyncWindowStartedAt", "SyncWindowRebasing", "IsLocalOnly") VALUES ('${calendarId}', '${accountId}', 'e2e-calendar', 'Calendar', '#0f62fe', 1, NULL, NULL, 0, 1);`,
		);

		await window.getByRole("button", { name: "Calendar", exact: true }).click();
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

test("calendar CRUD reaches a real CalDAV server through the Electron UI", async () => {
	const endpoint = `${calDavBase}${encodeURIComponent(calDavUser)}/${randomUUID()}/`;
	const createdCollection = await calDavRequest("MKCALENDAR", endpoint);
	expect(createdCollection.status).toBe(201);

	const { app, window, dataDirectory } = await launchApp();
	const title = `CalDAV ${Date.now()}`;
	const updatedTitle = `${title} updated`;

	try {
		await createImapAccount(window, imapPort, { endpoint });
		const database = join(dataDirectory, "app.db");
		await expect
			.poll(() =>
				Number(sqliteValue(database, 'SELECT COUNT(*) FROM "Calendars";')),
			)
			.toBeGreaterThanOrEqual(1);
		await window.getByRole("button", { name: "Calendar", exact: true }).click();
		await window
			.getByRole("button", { name: /^Add an event on /u })
			.first()
			.click();
		await window.getByLabel("Title").fill(title);
		await window.getByRole("button", { name: "Save" }).click();
		await expect(
			window.getByRole("button", { name: new RegExp(title, "u") }),
		).toBeVisible({
			timeout: 90_000,
		});

		let uid = "";
		await expect
			.poll(
				() => {
					uid = sqliteValue(
						database,
						`SELECT "ICalUid" FROM "CalendarEvents" WHERE "Title" = '${title}';`,
					);
					return uid;
				},
				{ timeout: 60_000 },
			)
			.not.toBe("");
		const resource = `${endpoint}${uid}.ics`;
		await expect
			.poll(async () => (await calDavRequest("GET", resource)).body)
			.toContain(`SUMMARY:${title}`);

		await window.getByRole("button", { name: new RegExp(title, "u") }).click();
		await window.getByLabel("Title").fill(updatedTitle);
		await window.getByRole("button", { name: "Save" }).click();
		await expect
			.poll(async () => (await calDavRequest("GET", resource)).body)
			.toContain(`SUMMARY:${updatedTitle}`);

		await window
			.getByRole("button", { name: new RegExp(updatedTitle, "u") })
			.click();
		await window.getByRole("button", { name: "Delete event" }).click();
		await expect
			.poll(async () => (await calDavRequest("GET", resource)).status)
			.toBe(404);
	} finally {
		await app.close();
		await calDavRequest("DELETE", endpoint);
	}
});

test("invite replies round-trip through SMTP and attendee replies update the organiser", async () => {
	await clearInbox(inviteImapPort);
	await fetch(`${mailpitApi}/messages`, { method: "DELETE" });

	const start = new Date();
	start.setMinutes(0, 0, 0);
	start.setHours(start.getHours() + 2);
	const end = new Date(start.getTime() + 60 * 60 * 1000);
	const requestUid = randomUUID();
	const requestTitle = `Invite round trip ${Date.now()}`;
	const organizerUid = randomUUID();
	const organizerTitle = `Organizer view ${Date.now()}`;
	const replySubject = `Accepted reply ${Date.now()}`;

	// Seed both messages before account creation. Initial coverage is deterministic; relying on
	// an IDLE wake here would turn this workflow test into a duplicate of the dedicated live-IDLE
	// scenario and make its calendar assertions depend on unrelated timing.
	await appendCalendarMessage(
		inviteImapPort,
		requestTitle,
		"Organizer <organizer@example.org>",
		"REQUEST",
		[
			"BEGIN:VCALENDAR",
			"VERSION:2.0",
			"PRODID:-//MyloMail E2E//EN",
			"METHOD:REQUEST",
			"BEGIN:VEVENT",
			`UID:${requestUid}`,
			`DTSTAMP:${icsDate(new Date())}`,
			`DTSTART:${icsDate(start)}`,
			`DTEND:${icsDate(end)}`,
			"SEQUENCE:0",
			`SUMMARY:${requestTitle}`,
			"ORGANIZER;CN=Organizer:mailto:organizer@example.org",
			`ATTENDEE;CN=Matrix User;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION:mailto:${calDavUser}`,
			"END:VEVENT",
			"END:VCALENDAR",
			"",
		].join("\r\n"),
	);
	await appendCalendarMessage(
		inviteImapPort,
		replySubject,
		"Bob Attendee <bob@example.org>",
		"REPLY",
		[
			"BEGIN:VCALENDAR",
			"VERSION:2.0",
			"PRODID:-//MyloMail E2E//EN",
			"METHOD:REPLY",
			"BEGIN:VEVENT",
			`UID:${organizerUid}`,
			`DTSTAMP:${icsDate(new Date())}`,
			`DTSTART:${icsDate(start)}`,
			`DTEND:${icsDate(end)}`,
			"SEQUENCE:0",
			`SUMMARY:${organizerTitle}`,
			`ORGANIZER;CN=Matrix User:mailto:${calDavUser}`,
			"ATTENDEE;CN=Bob Attendee;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED:mailto:bob@example.org",
			"END:VEVENT",
			"END:VCALENDAR",
			"",
		].join("\r\n"),
	);

	const { app, window, dataDirectory } = await launchApp();
	try {
		await createImapAccount(window, inviteImapPort);
		await expect(window.getByRole("button", { name: /INBOX/u })).toBeVisible({
			timeout: 60_000,
		});
		await window.getByRole("button", { name: /INBOX/u }).click();

		const database = join(dataDirectory, "app.db");
		await expect
			.poll(
				() =>
					Number(sqliteValue(database, 'SELECT COUNT(*) FROM "MessageRaws";')),
				{ timeout: 90_000 },
			)
			.toBeGreaterThanOrEqual(2);
		const accountId = sqliteValue(
			database,
			'SELECT "Id" FROM "Accounts" LIMIT 1;',
		);
		const calendarId = randomUUID().toUpperCase();
		executeSqlite(
			database,
			`INSERT INTO "Calendars" ("Id", "AccountId", "ProviderCalendarId", "Name", "Colour", "IsDefault", "SyncCursor", "SyncWindowStartedAt", "SyncWindowRebasing", "IsLocalOnly") VALUES ('${calendarId}', '${accountId}', 'local-invites', 'Invites', '#0f62fe', 1, NULL, NULL, 0, 1);`,
		);
		insertLocalInviteEvent(database, calendarId, {
			id: randomUUID(),
			uid: requestUid,
			title: requestTitle,
			start,
			end,
			organizerName: "Organizer",
			organizerEmail: "organizer@example.org",
			attendeeName: "Matrix User",
			attendeeEmail: calDavUser,
		});
		insertLocalInviteEvent(database, calendarId, {
			id: randomUUID(),
			uid: organizerUid,
			title: organizerTitle,
			start,
			end,
			organizerName: "Matrix User",
			organizerEmail: calDavUser,
			attendeeName: "Bob Attendee",
			attendeeEmail: "bob@example.org",
		});

		await window
			.getByRole("button", { name: new RegExp(replySubject, "u") })
			.click();
		await expect(
			window.getByText("This calendar reply could not be authenticated"),
		).toBeVisible();
		const acceptClaimed = window.getByRole("button", {
			name: "Accept claimed response",
		});
		await acceptClaimed.click();
		await expect(acceptClaimed).toBeEnabled();

		await window.getByRole("button", { name: "Calendar", exact: true }).click();
		await window
			.getByRole("button", { name: new RegExp(organizerTitle, "u") })
			.click();
		await expect(window.getByText("Bob Attendee")).toBeVisible();
		await expect(window.getByText("Accepted", { exact: true })).toBeVisible();
		await window.getByRole("button", { name: "Cancel", exact: true }).click();

		await window
			.getByRole("button", { name: new RegExp(requestTitle, "u") })
			.click();
		await expect(window.getByText("Respond to this invitation:")).toBeVisible();
		await window.getByRole("button", { name: "Accept", exact: true }).click();
		await expect(
			window.getByText("You accepted this invitation."),
		).toBeVisible();
		await expect
			.poll(
				async () => {
					const response = await fetch(`${mailpitApi}/messages`);
					const body = (await response.json()) as {
						messages: { Subject: string }[];
					};
					return body.messages.filter(
						(message) => message.Subject === `Accepted: ${requestTitle}`,
					).length;
				},
				{ timeout: 90_000 },
			)
			.toBe(1);
		const deliveredResponse = await fetch(`${mailpitApi}/messages`);
		const deliveredBody = (await deliveredResponse.json()) as {
			messages: { ID: string; Subject: string }[];
		};
		const delivered = deliveredBody.messages.find(
			(message) => message.Subject === `Accepted: ${requestTitle}`,
		);
		expect(delivered).toBeDefined();
		const messageResponse = await fetch(
			`${mailpitApi}/message/${delivered!.ID}`,
		);
		const message = (await messageResponse.json()) as {
			Attachments: {
				PartID: string;
				FileName: string;
				ContentType: string;
			}[];
		};
		const calendarAttachment = message.Attachments.find(
			(attachment) => attachment.ContentType === "text/calendar",
		);
		expect(calendarAttachment?.FileName).toBe("invite.ics");
		const partResponse = await fetch(
			`${mailpitApi}/message/${delivered!.ID}/part/${calendarAttachment!.PartID}`,
		);
		const calendarPart = await partResponse.text();
		expect(calendarPart).toContain("METHOD:REPLY");
		expect(calendarPart).toContain(`UID:${requestUid}`);
		expect(calendarPart).toContain(
			"ATTENDEE;CN=Matrix;PARTSTAT=ACCEPTED:mailto:test@mylomail.local",
		);
	} finally {
		await app.close();
	}
});
