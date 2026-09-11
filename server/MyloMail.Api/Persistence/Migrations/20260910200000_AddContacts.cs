using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260910200000_AddContacts")]
public partial class AddContacts : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(name: "Contacts", columns: table => new { Id = table.Column<Guid>(type: "TEXT", nullable: false), AccountId = table.Column<Guid>(type: "TEXT", nullable: false), DisplayName = table.Column<string>(type: "TEXT", nullable: false), ProviderContactId = table.Column<string>(type: "TEXT", nullable: true), ProviderRevision = table.Column<string>(type: "TEXT", nullable: true), SyncConflict = table.Column<bool>(type: "INTEGER", nullable: false) }, constraints: table => { table.PrimaryKey("PK_Contacts", x => x.Id); table.ForeignKey("FK_Contacts_Accounts_AccountId", x => x.AccountId, "Accounts", "Id", onDelete: ReferentialAction.Cascade); });
		migrationBuilder.CreateTable(name: "ContactAddresses", columns: table => new { Id = table.Column<Guid>(type: "TEXT", nullable: false), ContactId = table.Column<Guid>(type: "TEXT", nullable: false), Email = table.Column<string>(type: "TEXT", nullable: false), NormalizedEmail = table.Column<string>(type: "TEXT", nullable: false) }, constraints: table => { table.PrimaryKey("PK_ContactAddresses", x => x.Id); table.ForeignKey("FK_ContactAddresses_Contacts_ContactId", x => x.ContactId, "Contacts", "Id", onDelete: ReferentialAction.Cascade); });
		migrationBuilder.CreateIndex("IX_Contacts_AccountId_ProviderContactId", "Contacts", new[] { "AccountId", "ProviderContactId" }, unique: true);
		migrationBuilder.CreateIndex("IX_ContactAddresses_ContactId_NormalizedEmail", "ContactAddresses", new[] { "ContactId", "NormalizedEmail" }, unique: true);
		migrationBuilder.CreateIndex("IX_ContactAddresses_NormalizedEmail", "ContactAddresses", "NormalizedEmail");
	}
	protected override void Down(MigrationBuilder migrationBuilder) { migrationBuilder.DropTable("ContactAddresses"); migrationBuilder.DropTable("Contacts"); }
}
