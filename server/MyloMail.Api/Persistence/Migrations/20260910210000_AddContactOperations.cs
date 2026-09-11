using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260910210000_AddContactOperations")]
public partial class AddContactOperations : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "ProviderContainerId",
			table: "Contacts",
			type: "TEXT",
			nullable: true
		);
		migrationBuilder.AddColumn<int>(
			name: "MessageThreadBackfillVersion",
			table: "AppSettings",
			type: "INTEGER",
			nullable: false,
			defaultValue: 0
		);
		migrationBuilder.AddColumn<bool>(
			name: "HasProviderThreadId",
			table: "Messages",
			type: "INTEGER",
			nullable: false,
			defaultValue: false
		);
		migrationBuilder.Sql("""
			UPDATE "Messages"
			SET "HasProviderThreadId" = 1
			WHERE "ThreadId" IS NOT NULL;
			""");
		migrationBuilder.CreateTable(
			name: "ContactOperations",
			columns: table => new
			{
				Id = table.Column<Guid>(type: "TEXT", nullable: false),
				ContactId = table.Column<Guid>(type: "TEXT", nullable: false),
				Sequence = table.Column<long>(type: "INTEGER", nullable: false),
				Kind = table.Column<int>(type: "INTEGER", nullable: false),
				State = table.Column<int>(type: "INTEGER", nullable: false),
				DisplayName = table.Column<string>(type: "TEXT", nullable: false),
				EmailsJson = table.Column<string>(type: "TEXT", nullable: false),
				ExpectedRevision = table.Column<string>(type: "TEXT", nullable: true),
				CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
				DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
				SettledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_ContactOperations", x => x.Id);
				table.ForeignKey("FK_ContactOperations_Contacts_ContactId", x => x.ContactId, "Contacts", "Id", onDelete: ReferentialAction.Cascade);
			});
		migrationBuilder.CreateIndex("IX_ContactOperations_ContactId", "ContactOperations", "ContactId");
		migrationBuilder.CreateIndex("IX_ContactOperations_ContactId_Sequence", "ContactOperations", new[] { "ContactId", "Sequence" }, unique: true);
		migrationBuilder.CreateIndex("IX_ContactOperations_State_CreatedAt", "ContactOperations", new[] { "State", "CreatedAt" });
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable("ContactOperations");
		migrationBuilder.DropColumn("ProviderContainerId", "Contacts");
		migrationBuilder.DropColumn("MessageThreadBackfillVersion", "AppSettings");
		migrationBuilder.DropColumn("HasProviderThreadId", "Messages");
	}
}
