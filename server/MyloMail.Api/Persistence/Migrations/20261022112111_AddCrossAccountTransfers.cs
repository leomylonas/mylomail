using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddCrossAccountTransfers : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "CrossAccountTransfers",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					SourceAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					TargetAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					TargetMailboxId = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageIdHeader = table.Column<string>(type: "TEXT", nullable: true),
					IsRead = table.Column<bool>(type: "INTEGER", nullable: false),
					IsFlagged = table.Column<bool>(type: "INTEGER", nullable: false),
					BaselineCopies = table.Column<int>(type: "INTEGER", nullable: true),
					State = table.Column<int>(type: "INTEGER", nullable: false),
					Attempts = table.Column<int>(type: "INTEGER", nullable: false),
					LastError = table.Column<string>(type: "TEXT", nullable: true),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					DispatchedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
					CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_CrossAccountTransfers", x => x.Id);
					table.ForeignKey(
						name: "FK_CrossAccountTransfers_Accounts_SourceAccountId",
						column: x => x.SourceAccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_CrossAccountTransfers_Accounts_TargetAccountId",
						column: x => x.TargetAccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_CrossAccountTransfers_MessageId",
				table: "CrossAccountTransfers",
				column: "MessageId",
				unique: true,
				filter: "\"State\" IN (0, 1, 2)");

			migrationBuilder.CreateIndex(
				name: "IX_CrossAccountTransfers_SourceAccountId",
				table: "CrossAccountTransfers",
				column: "SourceAccountId");

			migrationBuilder.CreateIndex(
				name: "IX_CrossAccountTransfers_TargetAccountId_State",
				table: "CrossAccountTransfers",
				columns: new[] { "TargetAccountId", "State" });
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(
				name: "CrossAccountTransfers");
		}
	}
}
