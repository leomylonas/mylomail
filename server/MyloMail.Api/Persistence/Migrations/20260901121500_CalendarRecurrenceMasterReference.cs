using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260901121500_CalendarRecurrenceMasterReference")]
public partial class CalendarRecurrenceMasterReference : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
		name: "RecurrenceMasterProviderEventId", table: "CalendarEvents", type: "TEXT", nullable: true);

	protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
		name: "RecurrenceMasterProviderEventId", table: "CalendarEvents");
}
