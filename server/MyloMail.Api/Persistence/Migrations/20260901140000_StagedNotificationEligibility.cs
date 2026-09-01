using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class StagedNotificationEligibility : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropIndex(
				name: "IX_NotificationRecords_AccountId_MessageId_Kind",
				table: "NotificationRecords");

			migrationBuilder.AlterColumn<Guid>(
				name: "MessageId",
				table: "NotificationRecords",
				type: "TEXT",
				nullable: true,
				oldClrType: typeof(Guid),
				oldType: "TEXT");

			migrationBuilder.AddColumn<string>(
				name: "ProviderStableId",
				table: "NotificationRecords",
				type: "TEXT",
				nullable: true);

			migrationBuilder.CreateIndex(
				name: "IX_NotificationRecords_AccountId_MessageId_Kind",
				table: "NotificationRecords",
				columns: new[] { "AccountId", "MessageId", "Kind" },
				unique: true,
				filter: "\"MessageId\" IS NOT NULL");

			migrationBuilder.CreateIndex(
				name: "IX_NotificationRecords_AccountId_ProviderStableId_Kind",
				table: "NotificationRecords",
				columns: new[] { "AccountId", "ProviderStableId", "Kind" },
				unique: true,
				filter: "\"ProviderStableId\" IS NOT NULL");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropIndex(
				name: "IX_NotificationRecords_AccountId_MessageId_Kind",
				table: "NotificationRecords");

			migrationBuilder.DropIndex(
				name: "IX_NotificationRecords_AccountId_ProviderStableId_Kind",
				table: "NotificationRecords");

			migrationBuilder.DropColumn(
				name: "ProviderStableId",
				table: "NotificationRecords");

			migrationBuilder.AlterColumn<Guid>(
				name: "MessageId",
				table: "NotificationRecords",
				type: "TEXT",
				nullable: false,
				defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
				oldClrType: typeof(Guid),
				oldType: "TEXT",
				oldNullable: true);

			migrationBuilder.CreateIndex(
				name: "IX_NotificationRecords_AccountId_MessageId_Kind",
				table: "NotificationRecords",
				columns: new[] { "AccountId", "MessageId", "Kind" },
				unique: true);
		}
	}
}
