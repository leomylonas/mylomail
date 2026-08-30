using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class MutationModel : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "MutationExecutionAttempts",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					Provider = table.Column<int>(type: "INTEGER", nullable: false),
					OperationKind = table.Column<int>(type: "INTEGER", nullable: false),
					State = table.Column<int>(type: "INTEGER", nullable: false),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					ResultPersistedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MutationExecutionAttempts", x => x.Id);
					table.ForeignKey(
						name: "FK_MutationExecutionAttempts_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MutationItems",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Sequence = table.Column<long>(type: "INTEGER", nullable: false),
					OperationKind = table.Column<int>(type: "INTEGER", nullable: false),
					State = table.Column<int>(type: "INTEGER", nullable: false),
					TargetMailboxId = table.Column<Guid>(type: "TEXT", nullable: true),
					ScopeMailboxId = table.Column<Guid>(type: "TEXT", nullable: true),
					DesiredIsRead = table.Column<bool>(type: "INTEGER", nullable: true),
					DesiredIsFlagged = table.Column<bool>(type: "INTEGER", nullable: true),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LeaseOwner = table.Column<string>(type: "TEXT", nullable: true),
					LeaseExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					LastError = table.Column<string>(type: "TEXT", nullable: true),
					FailureCategory = table.Column<int>(type: "INTEGER", nullable: true),
					DependsOnMutationItemId = table.Column<Guid>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MutationItems", x => x.Id);
					table.ForeignKey(
						name: "FK_MutationItems_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MessagePendingChanges",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Field = table.Column<int>(type: "INTEGER", nullable: false),
					DesiredValue = table.Column<bool>(type: "INTEGER", nullable: false),
					MutationItemId = table.Column<Guid>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MessagePendingChanges", x => x.Id);
					table.ForeignKey(
						name: "FK_MessagePendingChanges_MutationItems_MutationItemId",
						column: x => x.MutationItemId,
						principalTable: "MutationItems",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateTable(
				name: "MutationExecutionAttemptItems",
				columns: table => new
				{
					AttemptId = table.Column<Guid>(type: "TEXT", nullable: false),
					MutationItemId = table.Column<Guid>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_MutationExecutionAttemptItems", x => new { x.AttemptId, x.MutationItemId });
					table.ForeignKey(
						name: "FK_MutationExecutionAttemptItems_MutationExecutionAttempts_AttemptId",
						column: x => x.AttemptId,
						principalTable: "MutationExecutionAttempts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_MutationExecutionAttemptItems_MutationItems_MutationItemId",
						column: x => x.MutationItemId,
						principalTable: "MutationItems",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_MessagePendingChanges_MessageId_Field",
				table: "MessagePendingChanges",
				columns: new[] { "MessageId", "Field" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_MessagePendingChanges_MutationItemId",
				table: "MessagePendingChanges",
				column: "MutationItemId");

			migrationBuilder.CreateIndex(
				name: "IX_MutationExecutionAttemptItems_MutationItemId",
				table: "MutationExecutionAttemptItems",
				column: "MutationItemId");

			migrationBuilder.CreateIndex(
				name: "IX_MutationExecutionAttempts_AccountId",
				table: "MutationExecutionAttempts",
				column: "AccountId");

			migrationBuilder.CreateIndex(
				name: "IX_MutationExecutionAttempts_State_ResultPersistedAt",
				table: "MutationExecutionAttempts",
				columns: new[] { "State", "ResultPersistedAt" });

			migrationBuilder.CreateIndex(
				name: "IX_MutationItems_AccountId_MessageId_Sequence",
				table: "MutationItems",
				columns: new[] { "AccountId", "MessageId", "Sequence" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_MutationItems_AccountId_State",
				table: "MutationItems",
				columns: new[] { "AccountId", "State" });

			migrationBuilder.CreateIndex(
				name: "IX_MutationItems_MessageId",
				table: "MutationItems",
				column: "MessageId");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(
				name: "MessagePendingChanges");

			migrationBuilder.DropTable(
				name: "MutationExecutionAttemptItems");

			migrationBuilder.DropTable(
				name: "MutationExecutionAttempts");

			migrationBuilder.DropTable(
				name: "MutationItems");
		}
	}
}
