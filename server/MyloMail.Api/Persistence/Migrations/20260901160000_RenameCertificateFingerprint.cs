using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations
{
	/// <inheritdoc />
	public partial class RenameCertificateFingerprint : Migration
	{
		/// <inheritdoc />
		protected override void Up(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropIndex(
				name: "IX_AccountTrustedCertificates_AccountId_Thumbprint",
				table: "AccountTrustedCertificates");

			migrationBuilder.RenameColumn(
				name: "Thumbprint",
				table: "AccountTrustedCertificates",
				newName: "Sha256Fingerprint");

			migrationBuilder.AddColumn<string>(
				name: "ExpectedHostname",
				table: "AccountTrustedCertificates",
				type: "TEXT",
				nullable: false,
				defaultValue: "");

			migrationBuilder.CreateIndex(
				name: "IX_AccountTrustedCertificates_AccountId_ExpectedHostname_Sha256Fingerprint",
				table: "AccountTrustedCertificates",
				columns: new[] { "AccountId", "ExpectedHostname", "Sha256Fingerprint" },
				unique: true);
		}

		/// <inheritdoc />
		protected override void Down(MigrationBuilder migrationBuilder)
		{
			migrationBuilder.DropIndex(
				name: "IX_AccountTrustedCertificates_AccountId_ExpectedHostname_Sha256Fingerprint",
				table: "AccountTrustedCertificates");

			migrationBuilder.DropColumn(
				name: "ExpectedHostname",
				table: "AccountTrustedCertificates");

			migrationBuilder.RenameColumn(
				name: "Sha256Fingerprint",
				table: "AccountTrustedCertificates",
				newName: "Thumbprint");

			migrationBuilder.CreateIndex(
				name: "IX_AccountTrustedCertificates_AccountId_Thumbprint",
				table: "AccountTrustedCertificates",
				columns: new[] { "AccountId", "Thumbprint" },
				unique: true);
		}
	}
}
