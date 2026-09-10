using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260910160000_AddChangeStreamRebaseState")]
public partial class AddChangeStreamRebaseState : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<bool>(
			name: "IsRebasing",
			table: "ChangeStreamStates",
			type: "INTEGER",
			nullable: false,
			defaultValue: false);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(name: "IsRebasing", table: "ChangeStreamStates");
	}
}
