using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

/// <summary>
/// Per-calendar "show in the unified calendar view" preference (§13 Epic 7), stored inverted
/// so every existing calendar defaults to visible without a data fix-up. A local UI preference
/// only — calendar sync never writes it.
/// </summary>
[DbContext(typeof(MyloMailDbContext))]
[Migration("20261004120000_AddCalendarIsHidden")]
public partial class AddCalendarIsHidden : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<bool>(
			name: "IsHidden",
			table: "Calendars",
			type: "INTEGER",
			nullable: false,
			defaultValue: false);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Raw SQL: EF's SQLite generator refuses DropColumn, and the column has no index or
		// constraint, so SQLite's own DROP COLUMN (3.35+) is sufficient.
		migrationBuilder.Sql("ALTER TABLE \"Calendars\" DROP COLUMN \"IsHidden\";");
	}
}
