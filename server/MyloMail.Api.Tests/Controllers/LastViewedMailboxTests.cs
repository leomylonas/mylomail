using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Controllers;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Persistence;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Controllers;

/// <summary>
/// The folder the mail view reopens to on launch. It is stored as a bare id with no foreign
/// key, so what is read back must be resolved against the topology as it is now — a removed
/// mailbox or a disabled account is "nothing remembered", never an error.
/// </summary>
public sealed class LastViewedMailboxTests
{
	[Fact]
	public async Task Nothing_is_remembered_before_a_folder_has_been_viewed()
	{
		await using var database = await MigratedAsync();

		var remembered = await ReadAsync(database);

		Assert.Null(remembered.MailboxId);
		Assert.Null(remembered.AccountId);
	}

	[Fact]
	public async Task A_stored_folder_is_read_back_with_its_account()
	{
		await using var database = await MigratedAsync();
		var (accountId, mailboxId) = await SeedMailboxAsync(database);

		Assert.IsType<NoContentResult>(await WriteAsync(database, mailboxId));
		var remembered = await ReadAsync(database);

		Assert.Equal(mailboxId, remembered.MailboxId);
		Assert.Equal(accountId, remembered.AccountId);
	}

	[Fact]
	public async Task The_latest_folder_replaces_the_previous_one()
	{
		await using var database = await MigratedAsync();
		var (_, first) = await SeedMailboxAsync(database);
		var (secondAccountId, second) = await SeedMailboxAsync(database);

		await WriteAsync(database, first);
		await WriteAsync(database, second);
		await WriteAsync(database, second);
		var remembered = await ReadAsync(database);

		Assert.Equal(second, remembered.MailboxId);
		Assert.Equal(secondAccountId, remembered.AccountId);
	}

	[Fact]
	public async Task An_unknown_mailbox_is_rejected_and_leaves_the_remembered_folder_alone()
	{
		await using var database = await MigratedAsync();
		var (_, mailboxId) = await SeedMailboxAsync(database);
		await WriteAsync(database, mailboxId);

		Assert.IsType<NotFoundResult>(await WriteAsync(database, Guid.NewGuid()));
		var remembered = await ReadAsync(database);

		Assert.Equal(mailboxId, remembered.MailboxId);
	}

	[Fact]
	public async Task A_removed_mailbox_reads_as_nothing_remembered_without_blocking_its_removal()
	{
		await using var database = await MigratedAsync();
		var (_, mailboxId) = await SeedMailboxAsync(database);
		await WriteAsync(database, mailboxId);

		await using (var scope = database.CreateScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await context.Mailboxes.Where(m => m.Id == mailboxId).ExecuteDeleteAsync();
		}
		var remembered = await ReadAsync(database);

		Assert.Null(remembered.MailboxId);
		Assert.Null(remembered.AccountId);
	}

	[Fact]
	public async Task A_disabled_account_reads_as_nothing_remembered_until_it_is_enabled_again()
	{
		await using var database = await MigratedAsync();
		var (accountId, mailboxId) = await SeedMailboxAsync(database);
		await WriteAsync(database, mailboxId);

		await SetEnabledAsync(database, accountId, false);
		Assert.Null((await ReadAsync(database)).MailboxId);

		await SetEnabledAsync(database, accountId, true);
		Assert.Equal(mailboxId, (await ReadAsync(database)).MailboxId);
	}

	private static async Task<TestDatabase> MigratedAsync()
	{
		var database = new TestDatabase();
		await database.MigrateAsync();
		return database;
	}

	private static async Task<(Guid AccountId, Guid MailboxId)> SeedMailboxAsync(
		TestDatabase database
	)
	{
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var account = new Account
		{
			Id = Guid.NewGuid(),
			DisplayName = "Account",
			ProviderType = ProviderType.Gmail,
			ProviderConfig = new GmailProviderConfig(),
		};
		var mailbox = new Mailbox
		{
			Id = Guid.NewGuid(),
			AccountId = account.Id,
			ProviderMailboxId = "INBOX",
			Name = "Inbox",
		};
		context.Accounts.Add(account);
		context.Mailboxes.Add(mailbox);
		await context.SaveChangesAsync();
		return (account.Id, mailbox.Id);
	}

	private static async Task SetEnabledAsync(TestDatabase database, Guid accountId, bool enabled)
	{
		await using var scope = database.CreateScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		await context
			.Accounts.Where(a => a.Id == accountId)
			.ExecuteUpdateAsync(setters => setters.SetProperty(a => a.IsEnabled, enabled));
	}

	private static async Task<IActionResult> WriteAsync(TestDatabase database, Guid mailboxId)
	{
		await using var scope = database.CreateScope();
		return await ControllerIn(scope)
			.PutLastViewedMailbox(new UpdateLastViewedMailboxRequest(mailboxId), default);
	}

	private static async Task<LastViewedMailboxDto> ReadAsync(TestDatabase database)
	{
		await using var scope = database.CreateScope();
		var result = await ControllerIn(scope).GetLastViewedMailbox(default);
		return Assert.IsType<LastViewedMailboxDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
	}

	private static AppSettingsController ControllerIn(AsyncServiceScope scope) =>
		new(
			scope.ServiceProvider.GetRequiredService<MyloMailDbContext>(),
			new RecordingHubEvents()
		);
}
