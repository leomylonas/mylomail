using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class SyncStaging : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "StagedChangeEvents",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					Ordinal = table.Column<long>(type: "INTEGER", nullable: false),
					Payload = table.Column<string>(type: "TEXT", nullable: false),
					StagedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					ScannedForNotifications = table.Column<bool>(type: "INTEGER", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_StagedChangeEvents", x => x.Id);
					table.ForeignKey(
						name: "FK_StagedChangeEvents_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_StagedChangeEvents_AccountId_Ordinal",
				table: "StagedChangeEvents",
				columns: new[] { "AccountId", "Ordinal" },
				unique: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(
				name: "StagedChangeEvents");
		}
	}
}
