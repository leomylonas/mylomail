using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// §13 Epic 10: "All actions reflected live across all open windows." Account settings, account
/// order, sidebar collapse and the per-mailbox sync overrides are one shared row each, not
/// per-window state — unlike panel layout and window bounds, which `AppSettingsController`
/// establishes as a deliberate read-once-at-open default. A window that changes one of them and
/// tells nobody leaves every other window rendering state the database no longer holds.
/// </summary>
public sealed class AccountStateBroadcastTests
{
	[Fact]
	public async Task Updating_account_settings_announces_the_account()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Events.Clear();

		var settings = Settings(harness.Account) with { DisplayName = "Renamed", Color = "#ff0000" };
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);

		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.Account.Id, announced.Id);
		Assert.Equal("Renamed", announced.DisplayName);
		Assert.Equal("#ff0000", announced.Color);
	}

	[Fact]
	public async Task Reordering_accounts_announces_only_the_accounts_that_moved()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var second = await AddSecondAccountAsync(harness);
		harness.Events.Clear();

		// Both accounts already hold the requested sort order, so the first call is a no-op.
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().ReorderAccounts([harness.Account.Id, second])
		);
		Assert.Empty(harness.Events.AccountStatuses);

		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().ReorderAccounts([second, harness.Account.Id])
		);
		Assert.Equal(2, harness.Events.AccountStatuses.Count);
		Assert.Equal(
			0,
			harness.Events.AccountStatuses.Single(account => account.Id == second).SortOrder
		);
		Assert.Equal(
			1,
			harness.Events.AccountStatuses.Single(account => account.Id == harness.Account.Id).SortOrder
		);
	}

	[Fact]
	public async Task Collapsing_an_account_announces_it_once()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetAccountSidebarCollapsed(harness.Account.Id, true)
		);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetAccountSidebarCollapsed(harness.Account.Id, true)
		);

		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.True(announced.SidebarCollapsed);
	}

	[Fact]
	public async Task Collapsing_a_mailbox_announces_the_mailbox_once()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		var mailboxId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Mailboxes.SingleAsync()).Id
		);
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetMailboxCollapsed(mailboxId, true)
		);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetMailboxCollapsed(mailboxId, true)
		);

		var announced = Assert.Single(harness.Events.Mailboxes);
		Assert.Equal(mailboxId, announced.Id);
		Assert.True(announced.IsCollapsed);
	}

	[Fact]
	public async Task Changing_a_mailbox_special_use_announces_the_override()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Archive");
		await SyncTests.ReconcileAsync(harness);
		var mailboxId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Mailboxes.SingleAsync()).Id
		);
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetMailboxSpecialUseOverride(mailboxId, SpecialUse.Archive)
		);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetMailboxSpecialUseOverride(mailboxId, SpecialUse.Archive)
		);

		var announced = Assert.Single(harness.Events.Mailboxes);
		Assert.Equal(mailboxId, announced.Id);
		Assert.Equal(SpecialUse.None, announced.SpecialUse);
		Assert.Equal(SpecialUse.Archive, announced.SpecialUseOverride);
	}

	/// <summary>
	/// Changing the bound resets coverage to <see cref="CoverageStatus.NotStarted"/>, which is
	/// what every window's sidebar reads to decide whether it is showing a bounded view.
	/// </summary>
	[Fact]
	public async Task Changing_a_mailbox_sync_bound_announces_its_reset_coverage()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);
		var mailboxId = await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().Mailboxes.SingleAsync()).Id
		);
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MailHub>()
				.SetMailboxInitialSyncOverride(mailboxId, InitialSyncMode.LastNMessages, 10)
		);

		var announced = Assert.Single(harness.Events.Mailboxes);
		Assert.Equal(mailboxId, announced.Id);
		Assert.Equal(CoverageStatus.NotStarted, announced.Coverage);
		Assert.Equal(InitialSyncMode.LastNMessages, announced.InitialSyncModeOverride);
	}

	private static async Task<Guid> AddSecondAccountAsync(SyncHarness harness) =>
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = new Account
			{
				Id = Guid.NewGuid(),
				DisplayName = "Second",
				ProviderType = ProviderType.Gmail,
				InitialSyncMode = InitialSyncMode.Full,
				SortOrder = 1,
			};
			context.Accounts.Add(account);
			await context.SaveChangesAsync();
			return account.Id;
		});

	private static AccountSettingsDto Settings(Account account) =>
		new(
			account.Id,
			account.DisplayName,
			account.Color,
			account.PollIntervalSeconds,
			account.PollingEnabled,
			account.UndoSendDelaySeconds,
			account.NotificationsEnabled,
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			AppendToSentOnSend: null
		);
}
