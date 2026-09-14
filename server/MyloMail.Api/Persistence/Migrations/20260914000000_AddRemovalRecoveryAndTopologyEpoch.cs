using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

/// <inheritdoc />
public partial class AddRemovalRecoveryAndTopologyEpoch : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.AddColumn<DateTimeOffset>(
			name: "LastCompletedWalkAt",
			table: "ChangeStreamStates",
			type: "TEXT",
			nullable: true
		);

		migrationBuilder.CreateTable(
			name: "AccountCredentialCleanups",
			columns: table => new
			{
				AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
			},
			constraints: table => table.PrimaryKey("PK_AccountCredentialCleanups", x => x.AccountId)
		);

		migrationBuilder.CreateTable(
			name: "MailboxTopologyEpochs",
			columns: table => new
			{
				AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
				ProviderMailboxId = table.Column<string>(type: "TEXT", nullable: false),
				Generation = table.Column<int>(type: "INTEGER", nullable: false),
			},
			constraints: table =>
			{
				table.PrimaryKey(
					"PK_MailboxTopologyEpochs",
					x => new { x.AccountId, x.ProviderMailboxId }
				);
				table.ForeignKey(
					"FK_MailboxTopologyEpochs_Accounts_AccountId",
					x => x.AccountId,
					"Accounts",
					"Id",
					onDelete: ReferentialAction.Cascade
				);
			}
		);
	}


	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropColumn(
			name: "LastCompletedWalkAt",
			table: "ChangeStreamStates"
		);

		migrationBuilder.DropTable(name: "AccountCredentialCleanups");
		migrationBuilder.DropTable(name: "MailboxTopologyEpochs");
	}
}
