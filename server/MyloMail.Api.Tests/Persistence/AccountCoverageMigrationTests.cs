using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Persistence;

/// <summary>
/// Gmail's per-label backfill cursors do not carry over to the account-wide walk (§3), so an
/// existing database has to be classified when the walk's state table appears: finished
/// accounts stay finished, mid-backfill accounts restart once, and nothing downloaded is lost.
/// </summary>
public sealed class AccountCoverageMigrationTests
{
	private const string PreviousMigration = "20261002005808_AddAccountGroupConversations";

	[Fact]
	public async Task A_gmail_account_whose_mailboxes_all_finished_stays_covered_and_is_not_walked_again()
	{
		await using var database = new TestDatabase();
		var accountId = Guid.NewGuid();
		var inboxId = Guid.NewGuid();
		var receiptsId = Guid.NewGuid();
		await SeedLegacyAsync(
			database,
			async context =>
			{
				AddGmailAccount(context, accountId);
				AddMailbox(context, inboxId, accountId, "INBOX", mode: InitialSyncMode.Full, bound: null);
				AddMailbox(context, receiptsId, accountId, "Receipts", mode: InitialSyncMode.LastNMonths, bound: 3);
				AddCoverage(context, inboxId, CoverageStatus.Covered, fetched: 40);
				AddCoverage(context, receiptsId, CoverageStatus.Covered, fetched: 7);
				await context.SaveChangesAsync();
			}
		);

		await database.MigrateAsync();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var walk = await context.AccountCoverageStates.SingleAsync();
		Assert.Equal(accountId, walk.AccountId);
		Assert.Equal(CoverageStatus.Covered, walk.Status);
		Assert.Null(walk.ResumeToken);

		var rows = await context.MailboxCoverageStates.ToListAsync();
		Assert.All(rows, row => Assert.Equal(CoverageStatus.Covered, row.Status));
		Assert.Equal(40, rows.Single(row => row.MailboxId == inboxId).MessagesFetched);

		// A single walk cannot honour a per-label range, so the setting is cleared rather than
		// left to show something that does nothing.
		var mailboxes = await context.Mailboxes.ToListAsync();
		Assert.All(mailboxes, mailbox => Assert.Null(mailbox.InitialSyncModeOverride));
		Assert.All(mailboxes, mailbox => Assert.Null(mailbox.InitialSyncBoundValueOverride));
	}

	[Fact]
	public async Task A_gmail_account_mid_backfill_restarts_its_walk_once_and_keeps_downloaded_mail()
	{
		await using var database = new TestDatabase();
		var accountId = Guid.NewGuid();
		var inboxId = Guid.NewGuid();
		var receiptsId = Guid.NewGuid();
		var messageId = Guid.NewGuid();
		await SeedLegacyAsync(
			database,
			async context =>
			{
				AddGmailAccount(context, accountId);
				AddMailbox(context, inboxId, accountId, "INBOX", mode: null, bound: null);
				AddMailbox(context, receiptsId, accountId, "Receipts", mode: null, bound: null);

				// One label finished, another is part-way through with a per-label cursor: the
				// shape of every account interrupted by this change.
				AddCoverage(context, inboxId, CoverageStatus.Covered, fetched: 40);
				AddCoverage(
					context,
					receiptsId,
					CoverageStatus.Backfilling,
					fetched: 5,
					estimated: 90,
					resumeToken: "per-label-provider-page",
					lastError: "earlier failure"
				);
				context.Messages.Add(
					new Message
					{
						Id = messageId,
						AccountId = accountId,
						ProviderStableId = "gmail-1",
						ReceivedAt = DateTimeOffset.UnixEpoch,
						Occurrences =
						[
							new MessageMailbox
							{
								Id = Guid.NewGuid(),
								MailboxId = inboxId,
								ProviderOccurrenceId = "gmail-1",
							},
						],
					}
				);
				await context.SaveChangesAsync();
			}
		);

		await database.MigrateAsync();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var walk = await context.AccountCoverageStates.SingleAsync();
		Assert.Equal(CoverageStatus.NotStarted, walk.Status);
		Assert.Null(walk.ResumeToken);
		Assert.Equal(0, walk.MessagesFetched);

		// The finished label is reset too: the account walk, not any label, owns coverage now,
		// and a Covered row beside a NotStarted walk would read as a late label.
		var rows = await context.MailboxCoverageStates.ToListAsync();
		Assert.Equal(2, rows.Count);
		Assert.All(rows, row =>
		{
			Assert.Equal(CoverageStatus.NotStarted, row.Status);
			Assert.Equal(0, row.MessagesFetched);
			Assert.Null(row.EstimatedTotal);
			Assert.Null(row.ResumeToken);
			Assert.Null(row.StartedAt);
			Assert.Null(row.LastError);
		});

		// Nothing already downloaded is touched: the restart replays it as upserts.
		var message = await context.Messages.Include(m => m.Occurrences).SingleAsync();
		Assert.Equal(messageId, message.Id);
		Assert.Single(message.Occurrences);
	}

	[Fact]
	public async Task A_gmail_account_with_no_mailboxes_yet_starts_not_started()
	{
		await using var database = new TestDatabase();
		var accountId = Guid.NewGuid();
		await SeedLegacyAsync(
			database,
			async context =>
			{
				AddGmailAccount(context, accountId);
				await context.SaveChangesAsync();
			}
		);

		await database.MigrateAsync();

		await using var scope = database.CreateScope();
		var walk = await scope
			.ServiceProvider.GetRequiredService<MyloMailDbContext>()
			.AccountCoverageStates.SingleAsync();
		Assert.Equal(CoverageStatus.NotStarted, walk.Status);
	}

	[Fact]
	public async Task Other_providers_coverage_is_left_exactly_as_it_was()
	{
		await using var database = new TestDatabase();
		var accountId = Guid.NewGuid();
		var mailboxId = Guid.NewGuid();
		await SeedLegacyAsync(
			database,
			async context =>
			{
				context.Accounts.Add(
					new Account
					{
						Id = accountId,
						DisplayName = "Graph",
						ProviderType = ProviderType.Microsoft365,
						ProviderConfig = new Microsoft365ProviderConfig(),
						InitialSyncMode = InitialSyncMode.Full,
					}
				);
				AddMailbox(context, mailboxId, accountId, "inbox", mode: InitialSyncMode.LastNMonths, bound: 6);
				AddCoverage(
					context,
					mailboxId,
					CoverageStatus.Backfilling,
					fetched: 20,
					estimated: 25,
					resumeToken: "https://graph.microsoft.test/messages?$skiptoken=page"
				);
				await context.SaveChangesAsync();
			}
		);

		await database.MigrateAsync();

		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		Assert.Empty(await context.AccountCoverageStates.ToListAsync());
		var coverage = await context.MailboxCoverageStates.SingleAsync();
		Assert.Equal(CoverageStatus.Backfilling, coverage.Status);
		Assert.Equal(20, coverage.MessagesFetched);
		Assert.Equal("https://graph.microsoft.test/messages?$skiptoken=page", coverage.ResumeToken);
		var mailbox = await context.Mailboxes.SingleAsync();
		Assert.Equal(InitialSyncMode.LastNMonths, mailbox.InitialSyncModeOverride);
		Assert.Equal(6, mailbox.InitialSyncBoundValueOverride);
	}

	/// <summary>
	/// Builds the schema, steps it back to the migration before the one under test, and seeds
	/// through the current model — the same schema an upgrading user's database has.
	/// </summary>
	private static async Task SeedLegacyAsync(TestDatabase database, Func<MyloMailDbContext, Task> seed)
	{
		await database.MigrateAsync();
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);
		await seed(context);
	}

	private static void AddGmailAccount(MyloMailDbContext context, Guid accountId) =>
		context.Accounts.Add(
			new Account
			{
				Id = accountId,
				DisplayName = "Gmail",
				ProviderType = ProviderType.Gmail,
				ProviderConfig = new GmailProviderConfig(),
				InitialSyncMode = InitialSyncMode.Full,
			}
		);

	private static void AddMailbox(
		MyloMailDbContext context,
		Guid mailboxId,
		Guid accountId,
		string providerMailboxId,
		InitialSyncMode? mode,
		int? bound
	) =>
		context.Mailboxes.Add(
			new Mailbox
			{
				Id = mailboxId,
				AccountId = accountId,
				ProviderMailboxId = providerMailboxId,
				Name = providerMailboxId,
				InitialSyncModeOverride = mode,
				InitialSyncBoundValueOverride = bound,
			}
		);

	private static void AddCoverage(
		MyloMailDbContext context,
		Guid mailboxId,
		CoverageStatus status,
		int fetched,
		int? estimated = null,
		string? resumeToken = null,
		string? lastError = null
	) =>
		context.MailboxCoverageStates.Add(
			new MailboxCoverageState
			{
				MailboxId = mailboxId,
				Status = status,
				MessagesFetched = fetched,
				EstimatedTotal = estimated,
				ResumeToken = resumeToken,
				StartedAt = DateTimeOffset.UnixEpoch,
				LastError = lastError,
			}
		);
}
