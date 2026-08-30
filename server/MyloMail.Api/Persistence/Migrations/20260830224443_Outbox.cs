using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class Outbox : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<Guid>(
				name: "OutboxItemId",
				table: "MutationExecutionAttempts",
				type: "TEXT",
				nullable: true);

			migrationBuilder.CreateTable(
				name: "OutboxItems",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					DraftId = table.Column<Guid>(type: "TEXT", nullable: false),
					Status = table.Column<int>(type: "INTEGER", nullable: false),
					ScheduledSendAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					StableMessageId = table.Column<string>(type: "TEXT", nullable: false),
					Attempts = table.Column<int>(type: "INTEGER", nullable: false),
					LastError = table.Column<string>(type: "TEXT", nullable: true),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					ReconcilingSince = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_OutboxItems", x => x.Id);
					table.ForeignKey(
						name: "FK_OutboxItems_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_MutationExecutionAttempts_OutboxItemId",
				table: "MutationExecutionAttempts",
				column: "OutboxItemId");

			migrationBuilder.CreateIndex(
				name: "IX_OutboxItems_AccountId_Status",
				table: "OutboxItems",
				columns: new[] { "AccountId", "Status" });

			migrationBuilder.CreateIndex(
				name: "IX_OutboxItems_StableMessageId",
				table: "OutboxItems",
				column: "StableMessageId",
				unique: true);

			migrationBuilder.AddForeignKey(
				name: "FK_MutationExecutionAttempts_OutboxItems_OutboxItemId",
				table: "MutationExecutionAttempts",
				column: "OutboxItemId",
				principalTable: "OutboxItems",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropForeignKey(
				name: "FK_MutationExecutionAttempts_OutboxItems_OutboxItemId",
				table: "MutationExecutionAttempts");

			migrationBuilder.DropTable(
				name: "OutboxItems");

			migrationBuilder.DropIndex(
				name: "IX_MutationExecutionAttempts_OutboxItemId",
				table: "MutationExecutionAttempts");

			migrationBuilder.DropColumn(
				name: "OutboxItemId",
				table: "MutationExecutionAttempts");
		}
	}
}
