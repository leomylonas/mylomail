using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	[DbContext(typeof(MyloMailDbContext))]
	[Migration("20260909143000_AddCalendarCreationRecovery")]
	public partial class AddCalendarCreationRecovery : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "CalendarCreationAttempts",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
					ICalUid = table.Column<string>(type: "TEXT", nullable: false),
					ProviderCreationKey = table.Column<string>(type: "TEXT", nullable: false),
					Title = table.Column<string>(type: "TEXT", nullable: false),
					Location = table.Column<string>(type: "TEXT", nullable: true),
					Description = table.Column<string>(type: "TEXT", nullable: true),
					Start = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					End = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
					DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_CalendarCreationAttempts", x => x.Id);
					table.ForeignKey(
						name: "FK_CalendarCreationAttempts_Calendars_CalendarId",
						column: x => x.CalendarId,
						principalTable: "Calendars",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_CalendarCreationAttempts_CalendarId",
				table: "CalendarCreationAttempts",
				column: "CalendarId");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(name: "CalendarCreationAttempts");
		}
	}
}
