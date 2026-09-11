using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260912010000_ResetLegacyGraphBoundedCoverage")]
public partial class ResetLegacyGraphBoundedCoverage : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
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
						AND account."ProviderType" = 2
						AND COALESCE(mailbox."InitialSyncModeOverride", account."InitialSyncMode") = 1
				);
			"""
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		// Data-state repair is intentionally irreversible.
	}
}
