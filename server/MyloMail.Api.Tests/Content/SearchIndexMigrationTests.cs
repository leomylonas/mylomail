using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
	public async Task Migrating_across_the_search_content_rebuild_leaves_the_index_intact()
	{
		await using var database = new TestDatabase();

		var services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(database.Directory)
			.BuildServiceProvider();
		await using var _ = services;

		var rowIds = new List<long>();
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context
				.GetService<IMigrator>()
				.MigrateAsync("20260830224443_Outbox");
			var accountId = Guid.NewGuid();
			await context.Database.ExecuteSqlInterpolatedAsync(
				$"""
				INSERT INTO "Accounts" (
					"Id", "AuthState", "CertificateTrustMode", "Color", "DisplayName",
					"InitialSyncMode", "IsEnabled", "NotificationsEnabled", "PollIntervalSeconds",
					"PollingEnabled", "ProviderType", "SortOrder", "UndoSendDelaySeconds"
				) VALUES (
					{accountId}, 0, 0, '', 'Test', 0, 1, 1, 60, 1, 0, 0, 5
				);
				"""
			);

			for (var i = 0; i < 3; i++)
			{
				var messageId = Guid.NewGuid();
				await context.Database.ExecuteSqlInterpolatedAsync(
					$"""
					INSERT INTO "Messages" (
						"Id", "AccountId", "Bcc", "Cc", "From", "HasNonInlineAttachments",
						"IsAnswered", "IsDraft", "IsFlagged", "IsRead", "RawFetched",
						"ReceivedAt", "ReplyToAddresses", "Snippet", "Subject", "To"
					) VALUES (
						{messageId}, {accountId}, '', '', '', 0, 0, 0, 0, 0, 0,
						{DateTimeOffset.UnixEpoch}, '', '', {$"Message {i}"}, ''
					);
					INSERT INTO "MessageSearchContents" (
						"MessageId", "Subject", "BodyText", "FromAddresses", "ToAddresses", "CcAddresses"
					) VALUES (
						{messageId}, {$"Message {i}"}, {$"body number {i}"}, '', '', ''
					);
					"""
				);
			}

			var rows = await context.MessageSearchContents.ToListAsync();
			rowIds.AddRange(rows.Select(row => row.RowId));
			foreach (var row in rows)
			{
				await context.Database.ExecuteSqlAsync(
					$"""
					INSERT INTO "MessageSearchIndex"("rowid", "Subject", "BodyText", "FromAddresses", "ToAddresses", "CcAddresses")
					VALUES ({row.RowId}, {row.Subject}, {row.BodyText}, '', '', '');
					"""
				);
			}
		}

		// Cross RestrictSearchContentDeletion in the released direction.
		await database.MigrateAsync();

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

			Assert.Equal(
				rowIds.Order(),
				await context.MessageSearchContents
					.OrderBy(content => content.RowId)
					.Select(content => content.RowId)
					.ToListAsync()
			);

			// The index still describes the rows it was built from.
			await context.Database.ExecuteSqlRawAsync(
				"INSERT INTO \"MessageSearchIndex\"(\"MessageSearchIndex\", \"rank\") VALUES ('integrity-check', 1);"
			);
		}
	}
}
