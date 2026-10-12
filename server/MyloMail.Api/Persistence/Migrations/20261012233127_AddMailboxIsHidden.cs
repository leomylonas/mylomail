using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddMailboxIsHidden : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<bool>(
				name: "IsHidden",
				table: "Mailboxes",
				type: "INTEGER",
				nullable: false,
				defaultValue: false);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL for the reason given in AddMaxMessageDownloadMegabytes.
			migrationBuilder.Sql("ALTER TABLE \"Mailboxes\" DROP COLUMN \"IsHidden\";");
		}
	}
}
