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

	[Fact]
	public async Task Restarts_only_legacy_bounded_Gmail_coverage_before_cursor_cutover()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = Guid.NewGuid();
		var boundedMailboxId = Guid.NewGuid();
		var fullMailboxId = Guid.NewGuid();

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context
				.GetService<IMigrator>()
				.MigrateAsync("20260911230000_AddResolvedMutationTarget");
			context.Accounts.Add(
				new Account
				{
					Id = accountId,
					DisplayName = "Gmail",
					ProviderType = ProviderType.Gmail,
					ProviderConfig = new GmailProviderConfig(),
					InitialSyncMode = InitialSyncMode.LastNMessages,
					InitialSyncBoundValue = 3,
				}
			);
			await context.SaveChangesAsync();
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "Mailboxes" (
					"Id", "AccountId", "ProviderMailboxId", "Name", "SpecialUse",
					"IsSubscribed", "LocalSortOrder", "IsCollapsed", "TopologyGeneration"
				)
				VALUES (
					{boundedMailboxId}, {accountId}, {"INBOX"}, {"Inbox"}, {0},
					{false}, {0}, {false}, {0}
				);
				"""
			);
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "Mailboxes" (
					"Id", "AccountId", "ProviderMailboxId", "Name", "SpecialUse",
					"IsSubscribed", "LocalSortOrder", "IsCollapsed", "TopologyGeneration",
					"InitialSyncModeOverride"
				)
				VALUES (
					{fullMailboxId}, {accountId}, {"ARCHIVE"}, {"Archive"}, {0},
					{false}, {0}, {false}, {0}, {(int)InitialSyncMode.Full}
				);
				"""
			);
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "MailboxCoverageStates" (
					"MailboxId", "Status", "MessagesFetched", "EstimatedTotal",
					"ResumeToken", "StartedAt", "LastError"
				)
				VALUES (
					{boundedMailboxId}, {(int)CoverageStatus.Backfilling}, {2}, {3},
					{"legacy-provider-page"}, {DateTimeOffset.UnixEpoch}, {"prior failure"}
				);
				"""
			);
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "MailboxCoverageStates" (
					"MailboxId", "Status", "MessagesFetched", "ResumeToken"
				)
				VALUES (
					{fullMailboxId}, {(int)CoverageStatus.Backfilling}, {2}, {"full-provider-page"}
				);
				"""
			);
		}

		await database.MigrateAsync();

		await using var verificationScope = database.CreateScope();
		var verificationContext =
			verificationScope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var bounded = await verificationContext.MailboxCoverageStates.SingleAsync(state =>
			state.MailboxId == boundedMailboxId
		);
		Assert.Equal(CoverageStatus.NotStarted, bounded.Status);
		Assert.Equal(0, bounded.MessagesFetched);
		Assert.Null(bounded.EstimatedTotal);
		Assert.Null(bounded.ResumeToken);
		Assert.Null(bounded.StartedAt);
		Assert.Null(bounded.LastError);

		var full = await verificationContext.MailboxCoverageStates.SingleAsync(state =>
			state.MailboxId == fullMailboxId
		);
		Assert.Equal("full-provider-page", full.ResumeToken);
		Assert.Equal(2, full.MessagesFetched);
	}

	[Fact]
	public async Task Restarts_legacy_count_bounded_Graph_coverage_before_cursor_cutover()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var accountId = Guid.NewGuid();
		var mailboxId = Guid.NewGuid();

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context
				.GetService<IMigrator>()
				.MigrateAsync("20260912000000_ResetLegacyGmailBoundedCoverage");
			context.Accounts.Add(
				new Account
				{
					Id = accountId,
					DisplayName = "Graph",
					ProviderType = ProviderType.Microsoft365,
					ProviderConfig = new Microsoft365ProviderConfig(),
					InitialSyncMode = InitialSyncMode.LastNMessages,
					InitialSyncBoundValue = 25,
				}
			);
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = accountId,
					ProviderMailboxId = "inbox",
					Name = "Inbox",
				}
			);
			context.MailboxCoverageStates.Add(
				new MailboxCoverageState
				{
					MailboxId = mailboxId,
					Status = CoverageStatus.Backfilling,
					MessagesFetched = 20,
					EstimatedTotal = 25,
					ResumeToken = "https://graph.microsoft.test/messages?$skiptoken=legacy",
				}
			);
			await context.SaveChangesAsync();
		}

		await database.MigrateAsync();

		await using var verificationScope = database.CreateScope();
		var coverage = await verificationScope.ServiceProvider
			.GetRequiredService<MyloMailDbContext>()
			.MailboxCoverageStates.SingleAsync();
		Assert.Equal(CoverageStatus.NotStarted, coverage.Status);
		Assert.Equal(0, coverage.MessagesFetched);
		Assert.Null(coverage.EstimatedTotal);
		Assert.Null(coverage.ResumeToken);
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
