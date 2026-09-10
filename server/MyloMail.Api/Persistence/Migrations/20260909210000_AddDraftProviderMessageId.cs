using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260909210000_AddDraftProviderMessageId")]
public partial class AddDraftProviderMessageId : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<string>(
			name: "ProviderMessageId",
			table: "Drafts",
			type: "TEXT",
			nullable: true);

		// Gmail has distinct draft-container and message ids. IMAP and Graph use one id for
		// both, so only their existing rows can be backfilled without inventing a false Gmail
		// container identity.
		migrationBuilder.Sql(
			"""
			UPDATE Drafts
			SET ProviderMessageId = ProviderDraftId
			WHERE ProviderMessageId IS NULL
				AND ProviderDraftId IS NOT NULL
				AND AccountId IN (
					SELECT Id FROM Accounts WHERE ProviderType <> 1
				);
			"""
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(name: "ProviderMessageId", table: "Drafts");
	}
}
