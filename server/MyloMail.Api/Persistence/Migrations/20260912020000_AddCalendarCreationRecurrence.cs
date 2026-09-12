using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260912020000_AddCalendarCreationRecurrence")]
public partial class AddCalendarCreationRecurrence : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "StartTimeZoneId",
			table: "CalendarCreationAttempts",
			type: "TEXT",
			nullable: true
		);
		migrationBuilder.AddColumn<string>(
			name: "EndTimeZoneId",
			table: "CalendarCreationAttempts",
			type: "TEXT",
			nullable: true
		);
		migrationBuilder.AddColumn<string>(
			name: "RecurrenceRules",
			table: "CalendarCreationAttempts",
			type: "TEXT",
			nullable: false,
			defaultValue: "[]"
		);
		migrationBuilder.AddColumn<string>(
			name: "RecurrenceDates",
			table: "CalendarCreationAttempts",
			type: "TEXT",
			nullable: false,
			defaultValue: "[]"
		);
		migrationBuilder.AddColumn<string>(
			name: "ExceptionDates",
			table: "CalendarCreationAttempts",
			type: "TEXT",
			nullable: false,
			defaultValue: "[]"
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			"""
			CREATE TABLE "__CalendarCreationAttempts_old" (
				"Id" TEXT NOT NULL CONSTRAINT "PK_CalendarCreationAttempts" PRIMARY KEY,
				"CalendarId" TEXT NOT NULL,
				"ICalUid" TEXT NOT NULL,
				"ProviderCreationKey" TEXT NOT NULL,
				"Title" TEXT NOT NULL,
				"Location" TEXT NULL,
				"Description" TEXT NULL,
				"Start" TEXT NOT NULL,
				"End" TEXT NOT NULL,
				"IsAllDay" INTEGER NOT NULL,
				"DispatchedAt" TEXT NOT NULL,
				CONSTRAINT "FK_CalendarCreationAttempts_Calendars_CalendarId"
					FOREIGN KEY ("CalendarId") REFERENCES "Calendars" ("Id") ON DELETE CASCADE
			);
			INSERT INTO "__CalendarCreationAttempts_old" (
				"Id", "CalendarId", "ICalUid", "ProviderCreationKey", "Title", "Location",
				"Description", "Start", "End", "IsAllDay", "DispatchedAt"
			)
			SELECT
				"Id", "CalendarId", "ICalUid", "ProviderCreationKey", "Title", "Location",
				"Description", "Start", "End", "IsAllDay", "DispatchedAt"
			FROM "CalendarCreationAttempts";
			DROP TABLE "CalendarCreationAttempts";
			ALTER TABLE "__CalendarCreationAttempts_old" RENAME TO "CalendarCreationAttempts";
			CREATE INDEX "IX_CalendarCreationAttempts_CalendarId"
				ON "CalendarCreationAttempts" ("CalendarId");
			"""
		);
	}
}
