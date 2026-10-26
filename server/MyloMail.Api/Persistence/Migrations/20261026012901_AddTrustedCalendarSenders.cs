using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddTrustedCalendarSenders : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "TrustedCalendarSenders",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					Scope = table.Column<int>(type: "INTEGER", nullable: false),
					Value = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_TrustedCalendarSenders", x => x.Id);
				});

			migrationBuilder.CreateIndex(
				name: "IX_TrustedCalendarSenders_Scope_Value",
				table: "TrustedCalendarSenders",
				columns: new[] { "Scope", "Value" },
				unique: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL for the reason given in AddMaxMessageDownloadMegabytes.
			migrationBuilder.Sql("DROP TABLE \"TrustedCalendarSenders\";");
		}
	}
}
