using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Content;

/// <summary>
/// Migrations must not desynchronise the search index.
/// </summary>
/// <remarks>
/// SQLite cannot alter a foreign key, so EF implements one as a table rebuild: a new table,
/// the rows copied across, the old one dropped. An FTS5 external-content index addresses its
/// content by rowid, so a rebuild that did not preserve them would leave every indexed term
/// pointing at the wrong row — silently, and only visible much later.
/// </remarks>
public sealed class SearchIndexMigrationTests
{
	[Fact]
	public async Task Re_running_migrations_over_indexed_content_leaves_the_index_intact()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();

		var services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(database.Directory)
			.BuildServiceProvider();
		await using var _ = services;

		var rowIds = new List<long>();
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var accountId = Guid.NewGuid();
			context.Accounts.Add(new Api.Domain.Account { Id = accountId, DisplayName = "Test" });

			for (var i = 0; i < 3; i++)
			{
				var messageId = Guid.NewGuid();
				context.Messages.Add(
					new Api.Domain.Message
					{
						Id = messageId,
						AccountId = accountId,
						Subject = $"Message {i}",
						ReceivedAt = DateTimeOffset.UnixEpoch,
					}
				);
				context.MessageSearchContents.Add(
					new Api.Domain.MessageSearchContent
					{
						MessageId = messageId,
						Subject = $"Message {i}",
						BodyText = $"body number {i}",
					}
				);
			}

			await context.SaveChangesAsync();
			rowIds.AddRange(await context.MessageSearchContents.Select(c => c.RowId).ToListAsync());

			foreach (var row in await context.MessageSearchContents.ToListAsync())
			{
				await context.Database.ExecuteSqlAsync(
					$"""
					INSERT INTO "MessageSearchIndex"("rowid", "Subject", "BodyText", "FromAddresses", "ToAddresses", "CcAddresses")
					VALUES ({row.RowId}, {row.Subject}, {row.BodyText}, '', '', '');
					"""
				);
			}
		}

		// Migrating again is what a released build does on every launch.
		await database.MigrateAsync();

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

			Assert.Equal(rowIds, await context.MessageSearchContents.Select(c => c.RowId).ToListAsync());

			// The index still describes the rows it was built from.
			await context.Database.ExecuteSqlRawAsync(
				"INSERT INTO \"MessageSearchIndex\"(\"MessageSearchIndex\", \"rank\") VALUES ('integrity-check', 1);"
			);
		}
	}
}
