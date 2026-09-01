using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddNotifications : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<DateTimeOffset>(
				name: "NotificationBaselineAt",
				table: "ChangeStreamStates",
				type: "TEXT",
				nullable: false,
				defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

			migrationBuilder.AddColumn<DateTimeOffset>(
				name: "NotificationEpoch",
				table: "Accounts",
				type: "TEXT",
				nullable: false,
				defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

			migrationBuilder.CreateTable(
				name: "NotificationRecords",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
					MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
					Kind = table.Column<int>(type: "INTEGER", nullable: false),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
					DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_NotificationRecords", x => x.Id);
					table.ForeignKey(
						name: "FK_NotificationRecords_Accounts_AccountId",
						column: x => x.AccountId,
						principalTable: "Accounts",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
					table.ForeignKey(
						name: "FK_NotificationRecords_Messages_MessageId",
						column: x => x.MessageId,
						principalTable: "Messages",
						principalColumn: "Id",
						onDelete: ReferentialAction.Cascade);
				});

			migrationBuilder.CreateIndex(
				name: "IX_NotificationRecords_AccountId_MessageId_Kind",
				table: "NotificationRecords",
				columns: new[] { "AccountId", "MessageId", "Kind" },
				unique: true);

			migrationBuilder.CreateIndex(
				name: "IX_NotificationRecords_DeliveredAt",
				table: "NotificationRecords",
				column: "DeliveredAt");

			migrationBuilder.CreateIndex(
				name: "IX_NotificationRecords_MessageId",
				table: "NotificationRecords",
				column: "MessageId");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(
				name: "NotificationRecords");

			migrationBuilder.DropColumn(
				name: "NotificationBaselineAt",
				table: "ChangeStreamStates");

			migrationBuilder.DropColumn(
				name: "NotificationEpoch",
				table: "Accounts");
		}
	}
}
