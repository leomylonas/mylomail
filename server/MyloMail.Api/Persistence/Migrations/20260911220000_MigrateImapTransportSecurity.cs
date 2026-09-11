using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyloMail.Api.Persistence.Migrations;

[DbContext(typeof(MyloMailDbContext))]
[Migration("20260911220000_MigrateImapTransportSecurity")]
public partial class MigrateImapTransportSecurity : Migration
{
	protected override void Up(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			"""
			UPDATE "Accounts"
			SET "ProviderConfig" = json_remove(
				json_set(
					"ProviderConfig",
					'$.ImapSecurity',
					CASE json_extract("ProviderConfig", '$.UseSsl')
						WHEN 1 THEN 1
						ELSE 2
					END,
					'$.AuthMethod',
					CASE lower(COALESCE(json_extract("ProviderConfig", '$.AuthMethod'), ''))
						WHEN 'oauth2' THEN 1
						ELSE 0
					END,
					'$.SmtpSecurity',
					2,
					'$.SmtpAuthMethod',
					1
				),
				'$.UseSsl'
			)
			WHERE json_extract("ProviderConfig", '$."$providerConfig"') = 'imap';
			"""
		);
	}

	protected override void Down(MigrationBuilder migrationBuilder)
	{
		migrationBuilder.Sql(
			"""
			UPDATE "Accounts"
			SET "ProviderConfig" = json_remove(
				json_set(
					"ProviderConfig",
					'$.UseSsl',
					CASE json_extract("ProviderConfig", '$.ImapSecurity')
						WHEN 1 THEN json('true')
						ELSE json('false')
					END,
					'$.AuthMethod',
					CASE json_extract("ProviderConfig", '$.AuthMethod')
						WHEN 1 THEN 'oauth2'
						ELSE 'password'
					END
				),
				'$.ImapSecurity',
				'$.SmtpSecurity',
				'$.SmtpAuthMethod'
			)
			WHERE json_extract("ProviderConfig", '$."$providerConfig"') = 'imap';
			"""
		);
	}
}
