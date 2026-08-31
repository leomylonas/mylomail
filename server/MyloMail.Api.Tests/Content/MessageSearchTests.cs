using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Content;

public sealed class MessageSearchTests : IAsyncLifetime
{
	private readonly TestDatabase database = new();
	private ServiceProvider services = null!;
	private Guid accountId;
	private Guid inboxId;
	private Guid archiveId;
	private Guid inboxMessageId;

	public async Task InitializeAsync()
	{
		await database.MigrateAsync();
		services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(database.Directory)
			.AddScoped<SearchIndexer>()
			.AddScoped<MessageSearch>()
			.BuildServiceProvider();

		accountId = Guid.NewGuid();
		inboxId = Guid.NewGuid();
		archiveId = Guid.NewGuid();
		inboxMessageId = Guid.NewGuid();
		var archivedMessageId = Guid.NewGuid();

		await using var scope = services.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		context.Accounts.Add(new Account { Id = accountId, DisplayName = "Test" });
		context.Mailboxes.Add(Mailbox(inboxId, "INBOX", SpecialUse.Inbox));
		context.Mailboxes.Add(Mailbox(archiveId, "ARCHIVE", SpecialUse.Archive));
		await context.SaveChangesAsync();

		await AddAsync(scope.ServiceProvider, inboxMessageId, inboxId, "Quarterly invoice", "the invoice is attached");
		await AddAsync(scope.ServiceProvider, archivedMessageId, archiveId, "Old invoice", "an archived invoice");
	}

	[Fact]
	public async Task Matching_messages_are_returned_across_mailboxes()
	{
		var results = await SearchAsync("invoice");

		Assert.Equal(2, results.Count);
	}

	/// <summary>
	/// Scoping is applied after the match, not indexed — which is what lets a message move
	/// folders without ever being reindexed (§8).
	/// </summary>
	[Fact]
	public async Task Results_can_be_scoped_to_one_mailbox()
	{
		var results = await SearchAsync("invoice", inboxId);

		Assert.Equal("Quarterly invoice", Assert.Single(results).Subject);
	}

	/// <summary>Moving a message changes what a scoped search finds, with no reindexing.</summary>
	[Fact]
	public async Task Moving_a_message_changes_its_scope_without_reindexing()
	{
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync(o => o.MessageId == inboxMessageId);
			occurrence.MailboxId = archiveId;
			await context.SaveChangesAsync();
		}

		Assert.Empty(await SearchAsync("Quarterly", inboxId));
		Assert.Single(await SearchAsync("Quarterly", archiveId));
	}

	/// <summary>Separate columns are what make field-scoped queries possible at all.</summary>
	[Fact]
	public async Task Field_scoped_queries_are_supported()
	{
		Assert.Single(await SearchAsync("Subject:Quarterly"));
		Assert.Empty(await SearchAsync("Subject:attached"));
	}

	/// <summary>A mistyped query is the user's to see, not an exception to leak.</summary>
	[Fact]
	public async Task A_malformed_query_returns_nothing_rather_than_throwing()
	{
		Assert.Empty(await SearchAsync("\"unbalanced"));
	}

	private async Task<IReadOnlyList<Api.Contracts.MessageSummaryDto>> SearchAsync(
		string query,
		Guid? mailboxId = null
	)
	{
		await using var scope = services.CreateAsyncScope();
		return await scope.ServiceProvider
			.GetRequiredService<MessageSearch>()
			.SearchAsync(accountId, query, mailboxId);
	}

	private Mailbox Mailbox(Guid id, string name, SpecialUse specialUse) =>
		new()
		{
			Id = id,
			AccountId = accountId,
			ProviderMailboxId = name,
			Name = name,
			SpecialUse = specialUse,
		};

	private async Task AddAsync(
		IServiceProvider scope,
		Guid messageId,
		Guid mailboxId,
		string subject,
		string body
	)
	{
		var context = scope.GetRequiredService<MyloMailDbContext>();
		context.Messages.Add(
			new Message
			{
				Id = messageId,
				AccountId = accountId,
				Subject = subject,
				ReceivedAt = DateTimeOffset.UnixEpoch,
				Occurrences =
				[
					new MessageMailbox
					{
						Id = Guid.NewGuid(),
						MailboxId = mailboxId,
						ProviderOccurrenceId = messageId.ToString(),
					},
				],
			}
		);
		await context.SaveChangesAsync();

		var mime = new MimeMessage();
		mime.From.Add(MailboxAddress.Parse("alice@example.org"));
		await scope
			.GetRequiredService<SearchIndexer>()
			.IndexAsync(
				messageId,
				mime,
				new MessageBody { MessageId = messageId, TextBody = body },
				CancellationToken.None
			);
	}

	public async Task DisposeAsync()
	{
		await services.DisposeAsync();
		await database.DisposeAsync();
	}
}
