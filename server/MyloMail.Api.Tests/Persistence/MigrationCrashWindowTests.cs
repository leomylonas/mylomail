using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

/// <summary>
/// The persistence crash window: a migration that fails part-way must always leave a
/// restorable copy of the database as it was before (§9).
/// </summary>
/// <remarks>
/// The failure is induced rather than mocked — the migration history is cleared so the
/// initial migration is re-applied over tables that already exist, which is what a
/// half-applied migration looks like on the next start.
/// </remarks>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class MigrationCrashWindowTests
{
	[Fact]
	public async Task A_failed_migration_leaves_a_restorable_backup_of_the_committed_data()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var accountId = Guid.NewGuid();
		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(
				new Account { Id = accountId, DisplayName = "Before", ProviderType = ProviderType.Imap }
			);
			await context.SaveChangesAsync();

			// Deliberately not checkpointed. In WAL mode the -wal file is part of the
			// persistent database state, which is exactly why the backup is taken with
			// VACUUM INTO rather than by copying the main file.
			await context.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\";");
		}

		await Assert.ThrowsAnyAsync<Exception>(database.MigrateAsync);

		var backupPath = database.DatabasePath + DatabaseBootstrapper.BackupSuffix;
		Assert.True(File.Exists(backupPath), "A failed migration must leave its pre-migration backup in place.");

		var options = new DbContextOptionsBuilder<MyloMailDbContext>()
			.UseSqlite($"Data Source={backupPath}")
			.Options;
		await using var restored = new MyloMailDbContext(options);

		Assert.Equal("Before", (await restored.Accounts.SingleAsync(a => a.Id == accountId)).DisplayName);
	}
}
