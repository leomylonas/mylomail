using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

/// <summary>
/// Gmail backfill becomes one account-wide walk (§3), so its cursor needs an account-scoped
/// home. The seed is what makes existing databases safe:
/// <list type="bullet">
/// <item>
/// An account whose every provider-backed mailbox already finished its per-label backfill is
/// seeded <c>Covered</c>. Without that, the first start after upgrade would walk a finished
/// mailbox again — the exact quota burn this change exists to stop.
/// </item>
/// <item>
/// Any other Gmail account was mid-backfill under per-label cursors, which mean nothing to an
/// account-wide walk. Its walk is seeded <c>NotStarted</c> and every mailbox row is reset, so
/// the walk restarts once. Messages already downloaded stay, and the restart replays them as
/// upserts; nothing is deleted.
/// </item>
/// <item>
/// Per-mailbox initial-sync overrides on Gmail mailboxes are cleared. A single walk cannot
/// honour them, and leaving them set would show the user a setting that does nothing.
/// </item>
/// </list>
/// </summary>
[DbContext(typeof(MyloMailDbContext))]
[Migration("20261003120000_AddAccountCoverageState")]
public partial class AddAccountCoverageState : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "AccountCoverageStates",
			columns: table => new
			{
				AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
				Status = table.Column<int>(type: "INTEGER", nullable: false),
				MessagesFetched = table.Column<int>(type: "INTEGER", nullable: false),
				EstimatedTotal = table.Column<int>(type: "INTEGER", nullable: true),
				ResumeToken = table.Column<string>(type: "TEXT", nullable: true),
				PolicyGeneration = table.Column<int>(type: "INTEGER", nullable: false),
				StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
				LastError = table.Column<string>(type: "TEXT", nullable: true)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_AccountCoverageStates", x => x.AccountId);
				table.ForeignKey(
					name: "FK_AccountCoverageStates_Accounts_AccountId",
					column: x => x.AccountId,
					principalTable: "Accounts",
					principalColumn: "Id",
					onDelete: ReferentialAction.Cascade);
			});

		// ProviderType 1 = Gmail; CoverageStatus 2 = Covered, 0 = NotStarted.
		migrationBuilder.Sql(
			"""
			INSERT INTO "AccountCoverageStates" (
				"AccountId", "Status", "MessagesFetched", "EstimatedTotal",
				"ResumeToken", "PolicyGeneration", "StartedAt", "LastError"
			)
			SELECT
				account."Id",
				CASE
					WHEN EXISTS (
						SELECT 1 FROM "Mailboxes" AS mailbox
						WHERE mailbox."AccountId" = account."Id"
							AND mailbox."ProviderMailboxId" IS NOT NULL
					)
					AND NOT EXISTS (
						SELECT 1 FROM "Mailboxes" AS mailbox
						LEFT JOIN "MailboxCoverageStates" AS coverage
							ON coverage."MailboxId" = mailbox."Id"
						WHERE mailbox."AccountId" = account."Id"
							AND mailbox."ProviderMailboxId" IS NOT NULL
							AND COALESCE(coverage."Status", 0) <> 2
					)
					THEN 2
					ELSE 0
				END,
				0, NULL, NULL, 0, NULL, NULL
			FROM "Accounts" AS account
			WHERE account."ProviderType" = 1;
			"""
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
			WHERE "MailboxId" IN (
				SELECT mailbox."Id"
				FROM "Mailboxes" AS mailbox
				JOIN "AccountCoverageStates" AS walk ON walk."AccountId" = mailbox."AccountId"
				WHERE walk."Status" = 0
			);
			"""
		);

		migrationBuilder.Sql(
			"""
			UPDATE "Mailboxes"
			SET "InitialSyncModeOverride" = NULL,
				"InitialSyncBoundValueOverride" = NULL
			WHERE "AccountId" IN (SELECT "Id" FROM "Accounts" WHERE "ProviderType" = 1);
			"""
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(name: "AccountCoverageStates");
	}
}
