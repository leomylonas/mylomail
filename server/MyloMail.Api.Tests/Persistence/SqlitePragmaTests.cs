using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

/// <summary>
/// The pragmas are per-connection, not per-database (§9), so "applied at startup" is not a
/// thing that can be true. These assert they are applied on <b>every</b> connection.
/// </summary>
public sealed class SqlitePragmaTests
{
	[Fact]
	public async Task Every_connection_gets_the_required_pragmas()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		// Two independent scopes, so this cannot pass by one connection being reused.
		for (var i = 0; i < 2; i++)
		{
			await using var scope = database.CreateScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var connection = context.Database.GetDbConnection();
			await context.Database.OpenConnectionAsync();

			foreach (var (pragma, expected) in SqlitePragmaInterceptor.Pragmas)
			{
				Assert.Equal(expected, await ReadPragmaAsync(connection, pragma));
			}
		}
	}

	/// <summary>
	/// <c>synchronous=FULL</c> is the durability guarantee the dispatch boundary depends on
	/// (§6). <c>NORMAL</c> — value 1 — explicitly trades power-loss durability for speed, so
	/// this asserts the exact value rather than merely "not OFF".
	/// </summary>
	[Fact]
	public async Task Synchronous_is_full_not_normal()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		await context.Database.OpenConnectionAsync();

		Assert.Equal("2", await ReadPragmaAsync(context.Database.GetDbConnection(), "synchronous"));
	}

	/// <summary>
	/// Foreign keys are off by default in SQLite, and the occurrence and content models
	/// depend on them, so a missing pragma shows up as orphan rows rather than as an error.
	/// </summary>
	[Fact]
	public async Task Foreign_keys_are_enforced()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

		await Assert.ThrowsAsync<SqliteException>(
			() =>
				context.Database.ExecuteSqlRawAsync(
					"""
					INSERT INTO "Mailboxes"
						("Id", "AccountId", "Name", "SpecialUse", "IsSubscribed", "LocalSortOrder", "TopologyGeneration")
					VALUES ('m', 'no-such-account', 'Inbox', 0, 1, 0, 0);
					"""
				)
		);
	}

	private static async Task<string?> ReadPragmaAsync(
		System.Data.Common.DbConnection connection,
		string pragma
	)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"PRAGMA {pragma};";
		var value = await command.ExecuteScalarAsync();
		return value?.ToString()?.ToLowerInvariant();
	}
}
