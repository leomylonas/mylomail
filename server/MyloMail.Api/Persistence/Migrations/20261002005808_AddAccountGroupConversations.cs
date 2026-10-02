using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddAccountGroupConversations : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<bool>(
				name: "GroupConversations",
				table: "Accounts",
				type: "INTEGER",
				nullable: false,
				defaultValue: false);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL for the reason given in AddMaxMessageDownloadMegabytes.
			migrationBuilder.Sql("ALTER TABLE \"Accounts\" DROP COLUMN \"GroupConversations\";");
		}
	}
}
