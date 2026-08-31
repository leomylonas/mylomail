using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class ServerSideDrafts : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<DateTimeOffset>(
				name: "PushedAt",
				table: "Drafts",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<bool>(
				name: "SyncConflict",
				table: "Drafts",
				type: "INTEGER",
				nullable: false,
				defaultValue: false);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "PushedAt",
				table: "Drafts");

			migrationBuilder.DropColumn(
				name: "SyncConflict",
				table: "Drafts");
		}
	}
}
