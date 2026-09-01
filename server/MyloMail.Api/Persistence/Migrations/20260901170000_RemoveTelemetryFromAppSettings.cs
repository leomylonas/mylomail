using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class RemoveTelemetryFromAppSettings : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropColumn(
				name: "OtelEndpoint",
				table: "AppSettings");

			migrationBuilder.DropColumn(
				name: "TelemetryEnabled",
				table: "AppSettings");
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<string>(
				name: "OtelEndpoint",
				table: "AppSettings",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<bool>(
				name: "TelemetryEnabled",
				table: "AppSettings",
				type: "INTEGER",
				nullable: false,
				defaultValue: false);
		}
	}
}
