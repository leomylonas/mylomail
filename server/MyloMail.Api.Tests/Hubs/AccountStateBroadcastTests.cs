using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
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
	public async Task Changing_account_sync_bound_restarts_only_inherited_mailbox_coverage()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("Archive", SpecialUse.Archive);
		harness.Provider.SeedMessage(
			"INBOX",
			Guid.NewGuid(),
			DateTimeOffset.UnixEpoch
		);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness, "INBOX");
		await SyncTests.CoverAsync(harness, "Archive");

		var mailboxIds = await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			return await context
				.Mailboxes.ToDictionaryAsync(mailbox => mailbox.ProviderMailboxId!, mailbox => mailbox.Id);
		});
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MailHub>()
				.SetMailboxInitialSyncOverride(
					mailboxIds["Archive"],
					InitialSyncMode.LastNMonths,
					6
				)
		);
		harness.Events.Clear();
		await harness.UsingAsync(services =>
		{
			((RecordingJobClient)services.GetRequiredService<Hangfire.IBackgroundJobClient>())
				.Created.Clear();
			return Task.CompletedTask;
		});

		var settings = Settings(harness.Account) with
		{
			InitialSyncMode = InitialSyncMode.LastNMessages,
			InitialSyncBoundValue = 25,
		};
		var applied = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);

		Assert.Equal(InitialSyncMode.LastNMessages, applied.InitialSyncMode);
		Assert.Equal(25, applied.InitialSyncBoundValue);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			Assert.Equal(InitialSyncMode.LastNMessages, account.InitialSyncMode);
			Assert.Equal(25, account.InitialSyncBoundValue);

			var inherited = await context.Mailboxes.SingleAsync(
				mailbox => mailbox.Id == mailboxIds["INBOX"]
			);
			var overridden = await context.Mailboxes.SingleAsync(
				mailbox => mailbox.Id == mailboxIds["Archive"]
			);
			Assert.Equal(1, inherited.CoveragePolicyGeneration);
			Assert.Equal(1, overridden.CoveragePolicyGeneration);
			Assert.Equal(
				CoverageStatus.NotStarted,
				(await context.MailboxCoverageStates.SingleAsync(
					coverage => coverage.MailboxId == inherited.Id
				)).Status
			);
			Assert.Equal(
				CoverageStatus.NotStarted,
				(await context.MailboxCoverageStates.SingleAsync(
					coverage => coverage.MailboxId == overridden.Id
				)).Status
			);
			Assert.Single(await context.Messages.ToListAsync());

			var jobs = ((RecordingJobClient)services.GetRequiredService<Hangfire.IBackgroundJobClient>())
				.Created.Where(job => job.Method.Name == nameof(SyncJobs.CoveragePageAsync))
				.ToList();
			var job = Assert.Single(jobs);
			Assert.Equal(inherited.Id, Assert.IsType<Guid>(job.Args[1]));
		});

		var mailboxAnnouncement = Assert.Single(harness.Events.Mailboxes);
		Assert.Equal(mailboxIds["INBOX"], mailboxAnnouncement.Id);
		Assert.Equal(CoverageStatus.NotStarted, mailboxAnnouncement.Coverage);
		Assert.Single(harness.Events.AccountStatuses);
	}

	[Fact]
	public async Task Account_sync_bounds_are_validated_and_full_history_clears_the_bound()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var settings = Settings(harness.Account) with
		{
			InitialSyncMode = InitialSyncMode.LastNMonths,
			InitialSyncBoundValue = 0,
		};

		await Assert.ThrowsAsync<HubException>(() =>
			harness.UsingAsync(services =>
				services.GetRequiredService<MailHub>().UpdateAccount(settings)
			)
		);

		settings = settings with { InitialSyncBoundValue = 3 };
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().UpdateAccount(settings)
		);
		var applied = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MailHub>()
				.UpdateAccount(
					settings with
					{
						InitialSyncMode = InitialSyncMode.Full,
						InitialSyncBoundValue = 999,
					}
				)
		);

		Assert.Equal(InitialSyncMode.Full, applied.InitialSyncMode);
		Assert.Null(applied.InitialSyncBoundValue);
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
			account.InitialSyncMode,
			account.InitialSyncBoundValue,
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			AppendToSentOnSend: null
		);
}
