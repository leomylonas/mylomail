using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddTrustedRemoteContentSenders : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.CreateTable(
				name: "TrustedRemoteContentSenders",
				columns: table => new
				{
					Id = table.Column<Guid>(type: "TEXT", nullable: false),
					Address = table.Column<string>(type: "TEXT", nullable: false),
					CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
				},
				constraints: table =>
				{
					table.PrimaryKey("PK_TrustedRemoteContentSenders", x => x.Id);
				});

			migrationBuilder.CreateIndex(
				name: "IX_TrustedRemoteContentSenders_Address",
				table: "TrustedRemoteContentSenders",
				column: "Address",
				unique: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropTable(
				name: "TrustedRemoteContentSenders");
		}
	}
}
