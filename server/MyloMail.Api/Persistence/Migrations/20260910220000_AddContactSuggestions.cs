using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260910220000_AddContactSuggestions")]
public partial class AddContactSuggestions : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "ProviderMissingSince",
			table: "Contacts",
			type: "TEXT",
			nullable: true
		);
		migrationBuilder.CreateTable(
			name: "ContactSuggestions",
			columns: table => new
			{
				AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
				NormalizedEmail = table.Column<string>(type: "TEXT", nullable: false),
				Email = table.Column<string>(type: "TEXT", nullable: false),
				DisplayName = table.Column<string>(type: "TEXT", nullable: false),
			},
			constraints: table =>
			{
				table.PrimaryKey(
					"PK_ContactSuggestions",
					x => new { x.AccountId, x.NormalizedEmail }
				);
				table.ForeignKey(
					name: "FK_ContactSuggestions_Accounts_AccountId",
					column: x => x.AccountId,
					principalTable: "Accounts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade
				);
			}
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable("ContactSuggestions");
		migrationBuilder.DropColumn("ProviderMissingSince", "Contacts");
	}
}
