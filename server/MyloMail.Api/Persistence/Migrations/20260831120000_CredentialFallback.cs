using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MyloMail.Api.Persistence;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

/// <inheritdoc />
[DbContext(typeof(MyloMailDbContext))]
[Migration("20260831120000_CredentialFallback")]
public partial class CredentialFallback : Migration
{
	/// <inheritdoc />
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.CreateTable(
			name: "CredentialFallbackSettings",
			columns: table => new
			{
				Id = table.Column<int>(type: "INTEGER", nullable: false),
				Salt = table.Column<byte[]>(type: "BLOB", nullable: false),
				VerifierNonce = table.Column<byte[]>(type: "BLOB", nullable: false),
				VerifierCiphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
				VerifierTag = table.Column<byte[]>(type: "BLOB", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_CredentialFallbackSettings", x => x.Id);
				table.CheckConstraint("CK_CredentialFallbackSettings_SingleRow", "\"Id\" = 1");
			});

		migrationBuilder.CreateTable(
			name: "EncryptedCredentials",
			columns: table => new
			{
				AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
				Nonce = table.Column<byte[]>(type: "BLOB", nullable: false),
				Ciphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
				Tag = table.Column<byte[]>(type: "BLOB", nullable: false)
			},
			constraints: table =>
			{
				table.PrimaryKey("PK_EncryptedCredentials", x => x.AccountId);
			});
	}

	/// <inheritdoc />
	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.DropTable(name: "CredentialFallbackSettings");
		migrationBuilder.DropTable(name: "EncryptedCredentials");
	}
}
