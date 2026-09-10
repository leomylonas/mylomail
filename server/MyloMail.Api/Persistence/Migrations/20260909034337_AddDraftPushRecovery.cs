using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddDraftPushRecovery : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<DateTimeOffset>(
				name: "PushDispatchedForSavedAt",
				table: "Drafts",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<string>(
				name: "StableMessageId",
				table: "Drafts",
				type: "TEXT",
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "PushDispatchedForSavedAt",
				table: "Drafts");

			migrationBuilder.DropColumn(
				name: "StableMessageId",
				table: "Drafts");
		}
	}
}
