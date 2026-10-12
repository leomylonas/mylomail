using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddMailboxPinnedSortOrder : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>(
				name: "PinnedSortOrder",
				table: "Mailboxes",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL for the reason given in AddMaxMessageDownloadMegabytes.
			migrationBuilder.Sql("ALTER TABLE \"Mailboxes\" DROP COLUMN \"PinnedSortOrder\";");
		}
	}
}
