using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

/// <summary>
/// The mailbox a plain shell window last selected, reopened on the next launch. A local UI
/// preference only: nullable, no foreign key, so mailbox deletion and topology replacement
/// never have to touch it — an id that no longer resolves simply reads as "nothing remembered".
/// </summary>
[DbContext(typeof(MyloMailDbContext))]
[Migration("20261004130000_AddLastViewedMailbox")]
public partial class AddLastViewedMailbox : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<Guid>(
			name: "LastViewedMailboxId",
			table: "AppSettings",
			type: "TEXT",
			nullable: true);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Raw SQL: EF's SQLite generator refuses DropColumn, and the column has no index or
		// constraint, so SQLite's own DROP COLUMN (3.35+) is sufficient.
		migrationBuilder.Sql("ALTER TABLE \"AppSettings\" DROP COLUMN \"LastViewedMailboxId\";");
	}
}
