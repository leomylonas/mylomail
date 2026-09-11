using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260911190000_AddOutboxRecipientSnapshot")]
public partial class AddOutboxRecipientSnapshot : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "RecipientSnapshot",
			table: "OutboxItems",
			type: "TEXT",
			nullable: false,
			defaultValue: "[]"
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn("RecipientSnapshot", "OutboxItems");
	}
}
