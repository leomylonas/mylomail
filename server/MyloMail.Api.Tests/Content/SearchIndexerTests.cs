using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Content;

/// <summary>The FTS5 external-content index (§8).</summary>
public sealed class SearchIndexerTests
{
	[Fact]
	public async Task An_indexed_message_is_findable_by_body_and_by_column()
	{
		await using var harness = new IndexHarness();
		await harness.IndexAsync("Quarterly report", "the numbers are attached", "alice@example.org");

		Assert.Equal(1, await harness.MatchCountAsync("numbers"));

		// Separate columns rather than one blob, so field-scoped search works at all.
		Assert.Equal(1, await harness.MatchCountAsync("Subject:Quarterly"));
		Assert.Equal(1, await harness.MatchCountAsync("FromAddresses:alice"));
		Assert.Equal(0, await harness.MatchCountAsync("Subject:numbers"));
		await harness.AssertIndexIsIntactAsync();
	}

	/// <summary>
	/// Re-indexing a message replaces its terms rather than corrupting the index.
	/// </summary>
	/// <remarks>
	/// An external-content index is updated by handing FTS5 the <b>old</b> column values so it
	/// can remove the terms it holds. An earlier version passed the new values, which deletes
	/// terms that were never indexed — SQLite then reports the mismatch as "database disk
	/// image is malformed" on the next write, nowhere near the cause.
	/// </remarks>
	[Fact]
	public async Task Re_indexing_replaces_the_old_terms()
	{
		await using var harness = new IndexHarness();
		await harness.IndexAsync("Original subject", "first body", "alice@example.org");
		await harness.IndexAsync("Replaced subject", "second body", "alice@example.org");

		Assert.Equal(0, await harness.MatchCountAsync("first"));
		Assert.Equal(1, await harness.MatchCountAsync("second"));
		Assert.Equal(0, await harness.MatchCountAsync("Subject:Original"));
		Assert.Equal(1, await harness.MatchCountAsync("Subject:Replaced"));

		// One content row, not two: this is an update, not an append.
		Assert.Equal(1, await harness.ContentRowCountAsync());

		// The authoritative check: FTS5 comparing its index against its content table. It
		// catches a mismatch where it is created, rather than on some later write.
		await harness.AssertIndexIsIntactAsync();
	}

	/// <summary>Removing a message removes its terms, or search returns hits pointing at nothing.</summary>
	[Fact]
	public async Task Removing_a_message_removes_it_from_the_index()
	{
		await using var harness = new IndexHarness();
		await harness.IndexAsync("Doomed", "about to vanish", "alice@example.org");
		await harness.RemoveAsync();

		Assert.Equal(0, await harness.MatchCountAsync("vanish"));
		Assert.Equal(0, await harness.ContentRowCountAsync());
	}

	/// <summary>
	/// A message cannot be deleted while it is still indexed.
	/// </summary>
	/// <remarks>
	/// The database refuses it. Deleting the content row without first removing the terms it
	/// mirrors leaves the index describing a row that no longer exists — searchable, and
	/// pointing at nothing — and FTS5 reports the mismatch as corruption on some later,
	/// unrelated write. Making it a foreign-key rule means tombstone collection cannot
	/// forget the ordering rather than merely being told not to (§6, §8).
	/// </remarks>
	[Fact]
	public async Task A_message_cannot_be_deleted_while_it_is_still_indexed()
	{
		await using var harness = new IndexHarness();
		await harness.IndexAsync("Doomed", "about to vanish", "alice@example.org");

		await Assert.ThrowsAsync<DbUpdateException>(harness.DeleteMessageRowAsync);
	}

	/// <summary>Removing it from the index first is what makes deletion possible.</summary>
	[Fact]
	public async Task Removing_from_the_index_first_allows_the_message_to_be_deleted()
	{
		await using var harness = new IndexHarness();
		await harness.IndexAsync("Doomed", "about to vanish", "alice@example.org");

		await harness.RemoveAsync();
		await harness.DeleteMessageRowAsync();

		Assert.Equal(0, await harness.MatchCountAsync("vanish"));
		await harness.AssertIndexIsIntactAsync();
	}

	private sealed class IndexHarness : IAsyncDisposable
	{
		private readonly TestDatabase database = new();
		private readonly ServiceProvider services;

		public IndexHarness()
		{
			services = new ServiceCollection()
				.AddLogging()
				.AddPersistence(database.Directory)
				.AddScoped<SearchIndexer>()
				.BuildServiceProvider();
			database.MigrateAsync().GetAwaiter().GetResult();
			SeedAsync().GetAwaiter().GetResult();
		}

		public Guid MessageId { get; } = Guid.NewGuid();

		private async Task SeedAsync()
		{
			await using var scope = services.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var accountId = Guid.NewGuid();

			context.Accounts.Add(new Account { Id = accountId, DisplayName = "Test" });
			context.Messages.Add(
				new Message
				{
					Id = MessageId,
					AccountId = accountId,
					ReceivedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		}

		public async Task IndexAsync(string subject, string body, string from)
		{
			await using var scope = services.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

			var message = await context.Messages.SingleAsync(m => m.Id == MessageId);
			message.Subject = subject;
			await context.SaveChangesAsync();

			var mime = new MimeMessage();
			mime.From.Add(MailboxAddress.Parse(from));
			mime.To.Add(MailboxAddress.Parse("test@example.org"));

			await scope.ServiceProvider
				.GetRequiredService<SearchIndexer>()
				.IndexAsync(
					MessageId,
					mime,
					new MessageBody { MessageId = MessageId, TextBody = body },
					CancellationToken.None
				);
		}

		public async Task RemoveAsync()
		{
			await using var scope = services.CreateAsyncScope();
			await scope.ServiceProvider
				.GetRequiredService<SearchIndexer>()
				.RemoveAsync(MessageId, CancellationToken.None);
		}

		public async Task<int> MatchCountAsync(string query)
		{
			await using var scope = services.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context.Database.OpenConnectionAsync();

			await using var command = context.Database.GetDbConnection().CreateCommand();
			command.CommandText =
				"SELECT COUNT(*) FROM \"MessageSearchIndex\" WHERE \"MessageSearchIndex\" MATCH $query;";
			var parameter = command.CreateParameter();
			parameter.ParameterName = "$query";
			parameter.Value = query;
			command.Parameters.Add(parameter);

			return Convert.ToInt32(await command.ExecuteScalarAsync());
		}

		/// <summary>Deletes the message the way any other code would, without touching the index.</summary>
		public async Task DeleteMessageRowAsync()
		{
			await using var scope = services.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync(m => m.Id == MessageId);
			context.Messages.Remove(message);
			await context.SaveChangesAsync();
		}

		/// <summary>
		/// Asks FTS5 whether its index still matches its content table.
		/// </summary>
		/// <remarks>
		/// The authoritative check, and the only one that catches a mismatch at the moment it
		/// is created rather than on some unrelated write much later.
		/// </remarks>
		public async Task AssertIndexIsIntactAsync()
		{
			await using var scope = services.CreateAsyncScope();
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context.Database.ExecuteSqlRawAsync(
				"INSERT INTO \"MessageSearchIndex\"(\"MessageSearchIndex\") VALUES ('integrity-check');"
			);
		}

		public async Task<int> ContentRowCountAsync()
		{
			await using var scope = services.CreateAsyncScope();
			return await scope.ServiceProvider
				.GetRequiredService<MyloMailDbContext>()
				.MessageSearchContents.CountAsync();
		}

		public async ValueTask DisposeAsync()
		{
			await services.DisposeAsync();
			await database.DisposeAsync();
		}
	}
}
