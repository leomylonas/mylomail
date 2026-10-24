using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddExportOptions : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.AddColumn<int>(
				name: "CatchUpPasses",
				table: "ExportJobs",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);

			migrationBuilder.AddColumn<DateTimeOffset>(
				name: "CompletedAt",
				table: "ExportJobs",
				type: "TEXT",
				nullable: true);

			migrationBuilder.AddColumn<int>(
				name: "FileNaming",
				table: "ExportJobs",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);

			migrationBuilder.AddColumn<int>(
				name: "Format",
				table: "ExportJobs",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);

			migrationBuilder.AddColumn<string>(
				name: "MailboxIdsJson",
				table: "ExportJobs",
				type: "TEXT",
				nullable: false,
				defaultValue: "[]");

			migrationBuilder.AddColumn<string>(
				name: "MboxOffsetsJson",
				table: "ExportJobs",
				type: "TEXT",
				nullable: false,
				defaultValue: "{}");

			migrationBuilder.AddColumn<int>(
				name: "SkippedCount",
				table: "ExportJobs",
				type: "INTEGER",
				nullable: false,
				defaultValue: 0);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			// Raw SQL for the reason given in AddMaxMessageDownloadMegabytes.
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"CatchUpPasses\";");
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"CompletedAt\";");
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"FileNaming\";");
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"Format\";");
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"MailboxIdsJson\";");
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"MboxOffsetsJson\";");
			migrationBuilder.Sql("ALTER TABLE \"ExportJobs\" DROP COLUMN \"SkippedCount\";");
		}
	}
}
