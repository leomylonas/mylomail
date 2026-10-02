using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddMaxMessageDownloadMegabytes : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>(
				name: "MaxMessageDownloadMegabytes",
				table: "Accounts",
				type: "INTEGER",
				nullable: false,
				defaultValue: 128);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL: EF's SQLite generator refuses DropColumn on this table, and the column has no
			// index or constraint, so SQLite's own DROP COLUMN (3.35+) is sufficient.
			migrationBuilder.Sql("ALTER TABLE \"Accounts\" DROP COLUMN \"MaxMessageDownloadMegabytes\";");
		}
	}
}
