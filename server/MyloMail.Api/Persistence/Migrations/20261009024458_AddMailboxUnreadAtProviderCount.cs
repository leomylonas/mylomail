using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddMailboxUnreadAtProviderCount : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>(
				name: "UnreadAtProviderCount",
				table: "Mailboxes",
				type: "INTEGER",
				nullable: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL for the reason given in AddMaxMessageDownloadMegabytes.
			migrationBuilder.Sql("ALTER TABLE \"Mailboxes\" DROP COLUMN \"UnreadAtProviderCount\";");
		}
	}
}
