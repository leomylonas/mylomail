using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class AddRemoteContentRules : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.Sql(
				"""
				CREATE TABLE "__RemoteContentRules_new" (
					"Id" TEXT NOT NULL CONSTRAINT "PK_RemoteContentRules" PRIMARY KEY,
					"Scope" INTEGER NOT NULL,
					"Decision" INTEGER NOT NULL,
					"Value" TEXT NOT NULL,
					"CreatedAt" TEXT NOT NULL,
					CONSTRAINT "CK_RemoteContentRules_Scope" CHECK ("Scope" IN (0, 1)),
					CONSTRAINT "CK_RemoteContentRules_Decision" CHECK ("Decision" IN (0, 1))
				);

				INSERT INTO "__RemoteContentRules_new" ("Id", "Scope", "Decision", "Value", "CreatedAt")
				SELECT min("Id"), 0, 0, lower("Address"), min("CreatedAt")
				FROM "TrustedRemoteContentSenders"
				GROUP BY lower("Address");

				DROP TABLE "TrustedRemoteContentSenders";
				ALTER TABLE "__RemoteContentRules_new" RENAME TO "RemoteContentRules";

				CREATE UNIQUE INDEX "IX_RemoteContentRules_Scope_Value"
				ON "RemoteContentRules" ("Scope", "Value");
				"""
			);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.Sql(
				"""
				CREATE TABLE "__TrustedRemoteContentSenders_new" (
					"Id" TEXT NOT NULL CONSTRAINT "PK_TrustedRemoteContentSenders" PRIMARY KEY,
					"Address" TEXT NOT NULL,
					"CreatedAt" TEXT NOT NULL
				);

				INSERT INTO "__TrustedRemoteContentSenders_new" ("Id", "Address", "CreatedAt")
				SELECT "Id", "Value", "CreatedAt"
				FROM "RemoteContentRules"
				WHERE "Scope" = 0 AND "Decision" = 0;

				DROP TABLE "RemoteContentRules";
				ALTER TABLE "__TrustedRemoteContentSenders_new"
					RENAME TO "TrustedRemoteContentSenders";

				CREATE UNIQUE INDEX "IX_TrustedRemoteContentSenders_Address"
				ON "TrustedRemoteContentSenders" ("Address");
				"""
			);
		}
	}
}
