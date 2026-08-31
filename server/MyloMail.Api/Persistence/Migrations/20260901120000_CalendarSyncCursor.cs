using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

/// <summary>Persists the CalDAV collection token with its authoritative local collection.</summary>
[DbContext(typeof(MyloMailDbContext))]
[Migration("20260901120000_CalendarSyncCursor")]
public partial class CalendarSyncCursor : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "SyncCursor",
			table: "Calendars",
			type: "TEXT",
			nullable: true);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(name: "SyncCursor", table: "Calendars");
	}
}
