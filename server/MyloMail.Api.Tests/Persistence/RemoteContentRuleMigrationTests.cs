using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

public sealed class RemoteContentRuleMigrationTests
{
	[Fact]
	public async Task Existing_trusted_senders_become_sender_allow_rules()
	{
		await using var database = new TestDatabase();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var migrator = context.GetService<IMigrator>();
		await migrator.MigrateAsync("20260909034337_AddDraftPushRecovery");
		await context.Database.ExecuteSqlRawAsync(
			"""
			INSERT INTO "TrustedRemoteContentSenders" ("Id", "Address", "CreatedAt")
			VALUES
				('00000000-0000-0000-0000-000000000001', 'Sender@Example.ORG', '2026-09-12 00:00:00+00:00'),
				('00000000-0000-0000-0000-000000000002', 'sender@example.org', '2026-09-12 01:00:00+00:00');
			"""
		);

		await migrator.MigrateAsync();
		context.ChangeTracker.Clear();

		var rule = await context.RemoteContentRules.SingleAsync();
		Assert.Equal("sender@example.org", rule.Value);
		Assert.Equal(RemoteContentRuleScope.Sender, rule.Scope);
		Assert.Equal(RemoteContentRuleDecision.Allow, rule.Decision);

		await Assert.ThrowsAsync<SqliteException>(() =>
			context.Database.ExecuteSqlRawAsync(
				"""
				INSERT INTO "RemoteContentRules" ("Id", "Scope", "Decision", "Value", "CreatedAt")
				VALUES ('00000000-0000-0000-0000-000000000003', 9, 0, 'example.org', '2026-09-12 00:00:00+00:00');
				"""
			)
		);
		await Assert.ThrowsAsync<SqliteException>(() =>
			context.Database.ExecuteSqlRawAsync(
				"""
				INSERT INTO "RemoteContentRules" ("Id", "Scope", "Decision", "Value", "CreatedAt")
				VALUES ('00000000-0000-0000-0000-000000000004', 1, 9, 'example.org', '2026-09-12 00:00:00+00:00');
				"""
			)
		);
	}
}
