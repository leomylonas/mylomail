using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

public sealed class DatabaseBootstrapperTests
{
	[Fact]
	public async Task Creates_the_schema_including_the_fts_index()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		Assert.Contains("MessageSearchIndex", await TableNamesAsync(database));
		Assert.Contains("Messages", await TableNamesAsync(database));
	}

	/// <summary>
	/// Migrating a database that already holds data is the case that matters: the first run
	/// creates an empty file, and every run after that is the one that can lose it.
	/// </summary>
	[Fact]
	public async Task Migrates_an_existing_populated_database_without_losing_data()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var accountId = Guid.NewGuid();
		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(
				new Account
				{
					Id = accountId,
					DisplayName = "Test",
					ProviderType = ProviderType.Imap,
					ProviderConfig = new ImapProviderConfig { Host = "imap.example.org", Port = 993 },
				}
			);
			await context.SaveChangesAsync();
		}

		await database.MigrateAsync();

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == accountId);
			Assert.Equal("imap.example.org", Assert.IsType<ImapProviderConfig>(account.ProviderConfig).Host);
		}
	}

	[Fact]
	public async Task Migrates_legacy_IMAP_transport_settings_to_encrypted_explicit_modes()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = Guid.NewGuid();

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context
				.GetService<IMigrator>()
				.MigrateAsync("20260911210000_AddContactSyncCursor");
			context.Accounts.Add(
				new Account
				{
					Id = accountId,
					DisplayName = "Legacy",
					ProviderType = ProviderType.Imap,
					ProviderConfig = new ImapProviderConfig
					{
						Host = "imap.example.org",
						Port = 143,
						UserName = "someone@example.org",
						SmtpHost = "smtp.example.org",
						SmtpPort = 587,
					},
				}
			);
			await context.SaveChangesAsync();
			const string legacyConfig =
				"""{"$providerConfig":"imap","Host":"imap.example.org","Port":143,"UseSsl":false,"UserName":"someone@example.org","AuthMethod":"","SmtpHost":"smtp.example.org","SmtpPort":587,"AppendToSentOnSend":true,"SmtpCredentialSource":0}""";
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"""UPDATE "Accounts" SET "ProviderConfig" = {legacyConfig} WHERE "Id" = {accountId};"""
			);
		}

		await database.MigrateAsync();

		await using var verificationScope = database.CreateScope();
		var verificationContext =
			verificationScope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var account = await verificationContext.Accounts.SingleAsync(a => a.Id == accountId);
		var config = Assert.IsType<ImapProviderConfig>(account.ProviderConfig);
		Assert.Equal(MailTransportSecurity.StartTls, config.ImapSecurity);
		Assert.Equal(ImapAuthMethod.Password, config.AuthMethod);
		Assert.Equal(MailTransportSecurity.StartTls, config.SmtpSecurity);
		Assert.Equal(SmtpAuthMethod.Password, config.SmtpAuthMethod);
	}

	/// <summary>
	/// The backup exists only for the duration of the migration; leaving it behind on
	/// success would make the "a backup means a migration failed" signal meaningless.
	/// </summary>
	[Fact]
	public async Task Removes_the_pre_migration_backup_once_migration_succeeds()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		await database.MigrateAsync();

		Assert.False(File.Exists(database.DatabasePath + DatabaseBootstrapper.BackupSuffix));
	}

	/// <summary>
	/// A leftover backup is the only copy predating a failed migration, so starting again
	/// must refuse rather than overwrite it.
	/// </summary>
	[Fact]
	public async Task Refuses_to_start_over_a_leftover_backup()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		await File.WriteAllTextAsync(database.DatabasePath + DatabaseBootstrapper.BackupSuffix, "");

		await Assert.ThrowsAsync<InvalidOperationException>(database.MigrateAsync);
	}

	private static async Task<List<string>> TableNamesAsync(TestDatabase database)
	{
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		await context.Database.OpenConnectionAsync();

		await using var command = context.Database.GetDbConnection().CreateCommand();
		command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";

		var names = new List<string>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync())
		{
			names.Add(reader.GetString(0));
		}

		return names;
	}
}
