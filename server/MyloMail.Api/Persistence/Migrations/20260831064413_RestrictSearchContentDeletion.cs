using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class RestrictSearchContentDeletion : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropForeignKey(
				name: "FK_MessageSearchContents_Messages_MessageId",
				table: "MessageSearchContents");

			migrationBuilder.AddForeignKey(
				name: "FK_MessageSearchContents_Messages_MessageId",
				table: "MessageSearchContents",
				column: "MessageId",
				principalTable: "Messages",
				principalColumn: "Id",
				onDelete: ReferentialAction.Restrict);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropForeignKey(
				name: "FK_MessageSearchContents_Messages_MessageId",
				table: "MessageSearchContents");

			migrationBuilder.AddForeignKey(
				name: "FK_MessageSearchContents_Messages_MessageId",
				table: "MessageSearchContents",
				column: "MessageId",
				principalTable: "Messages",
				principalColumn: "Id",
				onDelete: ReferentialAction.Cascade);
		}
	}
}
