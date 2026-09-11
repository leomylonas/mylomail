using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260912000000_ResetLegacyGmailBoundedCoverage")]
public partial class ResetLegacyGmailBoundedCoverage : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<int>(
			name: "CoveragePolicyGeneration",
			table: "Mailboxes",
			type: "INTEGER",
			nullable: false,
			defaultValue: 0
		);

		migrationBuilder.Sql(
			"""
			UPDATE "MailboxCoverageStates"
			SET "Status" = 0,
				"MessagesFetched" = 0,
				"EstimatedTotal" = NULL,
				"ResumeToken" = NULL,
				"StartedAt" = NULL,
				"LastError" = NULL
			WHERE "ResumeToken" IS NOT NULL
				AND EXISTS (
					SELECT 1
					FROM "Mailboxes" AS mailbox
					JOIN "Accounts" AS account ON account."Id" = mailbox."AccountId"
					WHERE mailbox."Id" = "MailboxCoverageStates"."MailboxId"
						AND account."ProviderType" = 1
						AND COALESCE(mailbox."InitialSyncModeOverride", account."InitialSyncMode") = 1
				);
			"""
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			"""ALTER TABLE "Mailboxes" DROP COLUMN "CoveragePolicyGeneration";"""
		);
	}
}
