using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Gmail's coverage has exactly one owner per account (§3): one self-scheduling job that walks
/// the account, then catches up any label the walk did not cover. IMAP and Graph keep an owner
/// per mailbox (see <see cref="TopologyReschedulingTests"/>).
/// </summary>
public sealed class AccountCoverageJobTests
{
	[Fact]
	public async Task The_job_chains_itself_while_pages_remain_and_keeps_its_single_owner_until_the_end()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AccountWalkPageLimit = 2;
		for (var index = 0; index < 3; index++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(index));
		}
		await Prepare(harness);
		Assert.True(await ClaimAsync(harness));

		await RunJobAsync(harness);

		var created = await CreatedJobsAsync(harness);
		Assert.Single(created, job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync));
		Assert.Contains(created, job => job.Method.Name == nameof(ContentJobs.FetchNextAsync));
		Assert.DoesNotContain(created, job => job.Method.Name == nameof(SyncJobs.ReplayStagedAsync));
		Assert.False(await ClaimAsync(harness), "the owner is held while pages remain");

		await RunJobAsync(harness);

		created = await CreatedJobsAsync(harness);
		Assert.Contains(created, job => job.Method.Name == nameof(SyncJobs.ReplayStagedAsync));
		Assert.Contains(created, job => job.Method.Name == nameof(SyncJobs.ChangeStreamAsync));
		Assert.True(await ClaimAsync(harness), "the owner is released once coverage is complete");
	}

	/// <summary>
	/// The quota-relevant case: a label created on a finished account is caught up on its own.
	/// It must never restart the account walk, which would fetch every message again.
	/// </summary>
	[Fact]
	public async Task A_new_label_on_a_covered_account_is_caught_up_without_walking_the_account_again()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var index = 0; index < 3; index++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(index));
		}
		await Prepare(harness);
		await SyncTests.CoverAsync(harness);
		var walkedBefore = harness.Provider.AccountWalkMessageIds.Count;
		var pagesBefore = harness.Provider.AccountWalkPages;
		harness.Provider.AddMailbox("FRESH");
		harness.Provider.SeedMessage("FRESH", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddDays(1));
		await ClearJobsAsync(harness);

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		var created = await CreatedJobsAsync(harness);
		Assert.Single(created, job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync));
		Assert.DoesNotContain(created, job => job.Method.Name == nameof(SyncJobs.CoveragePageAsync));

		await RunJobAsync(harness);

		Assert.Equal(walkedBefore, harness.Provider.AccountWalkMessageIds.Count);
		Assert.Equal(pagesBefore, harness.Provider.AccountWalkPages);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(CoverageStatus.Covered, (await context.AccountCoverageStates.SingleAsync()).Status);
			var fresh = await harness.MailboxAsync(scope, "FRESH");
			Assert.Equal(
				CoverageStatus.Covered,
				(await context.MailboxCoverageStates.SingleAsync(c => c.MailboxId == fresh.Id)).Status
			);
			Assert.Equal(1, await context.MessageMailboxes.CountAsync(o => o.MailboxId == fresh.Id));
		});
	}

	[Fact]
	public async Task A_covered_account_has_nothing_to_start()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await Prepare(harness);
		await SyncTests.CoverAsync(harness);
		await ClearJobsAsync(harness);

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		Assert.DoesNotContain(
			await CreatedJobsAsync(harness),
			job => job.Method.Name is nameof(SyncJobs.AccountCoveragePageAsync) or nameof(SyncJobs.CoveragePageAsync)
		);
	}

	/// <summary>
	/// A bound change restarts the one account walk and resets every mailbox — including one
	/// carrying a per-mailbox range from before the walk existed — and starts one owner, not one
	/// per mailbox. A second change while that owner is alive starts nothing further.
	/// </summary>
	[Fact]
	public async Task A_settings_change_restarts_one_account_walk_and_resets_every_mailbox()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("RECEIPTS");
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await Prepare(harness);
		await SyncTests.CoverAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var receipts = await harness.MailboxAsync(scope, "RECEIPTS");
			receipts.InitialSyncModeOverride = InitialSyncMode.LastNMonths;
			receipts.InitialSyncBoundValueOverride = 3;
			await context.SaveChangesAsync();
		});
		await ClearJobsAsync(harness);

		await ChangeBoundAsync(harness, 50);
		await ChangeBoundAsync(harness, 60);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
			Assert.Equal(2, walk.PolicyGeneration);
			Assert.Null(walk.ResumeToken);
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				row => Assert.Equal(CoverageStatus.NotStarted, row.Status)
			);
			Assert.All(
				await context.Mailboxes.ToListAsync(),
				mailbox => Assert.Equal(2, mailbox.CoveragePolicyGeneration)
			);
			var account = await context.Accounts.SingleAsync();
			Assert.Equal(InitialSyncMode.LastNMessages, account.InitialSyncMode);
			Assert.Equal(60, account.InitialSyncBoundValue);
		});
		var created = await CreatedJobsAsync(harness);
		Assert.Single(created, job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync));
		Assert.DoesNotContain(created, job => job.Method.Name == nameof(SyncJobs.CoveragePageAsync));
	}

	[Fact]
	public async Task A_throttled_account_defers_the_walk_without_releasing_its_owner()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await Prepare(harness);
		Assert.True(await ClaimAsync(harness));
		await harness.UsingAsync(scope =>
		{
			scope.GetRequiredService<AccountGate>().Throttle(harness.Account.Id, TimeSpan.FromSeconds(42));
			return Task.CompletedTask;
		});

		await RunJobAsync(harness);

		Assert.Equal(0, harness.Provider.AccountWalkPages);
		Assert.Single(
			await CreatedJobsAsync(harness),
			job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync)
		);
		Assert.False(await ClaimAsync(harness));
	}

	[Fact]
	public async Task A_paused_account_releases_its_owner_and_does_no_work()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await Prepare(harness);
		Assert.True(await ClaimAsync(harness));
		await harness.UsingAsync(async scope =>
		{
			var account = await harness.AccountInScopeAsync(scope);
			account.PollingEnabled = false;
			await scope.GetRequiredService<MyloMailDbContext>().SaveChangesAsync();
		});

		await RunJobAsync(harness);

		Assert.Equal(0, harness.Provider.AccountWalkPages);
		Assert.Empty(await CreatedJobsAsync(harness));
		Assert.True(await ClaimAsync(harness));
	}

	/// <summary>
	/// A provider error is recorded against the walk and retried by the same owner, which keeps
	/// its claim — releasing it would let a starter race a second walker onto the same cursor.
	/// </summary>
	[Fact]
	public async Task A_provider_failure_is_recorded_and_retried_while_the_owner_is_held()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await Prepare(harness);
		Assert.True(await ClaimAsync(harness));
		harness.Provider.BeforeInitialSyncReturnAsync = () =>
			Task.FromException(new InvalidOperationException("Malformed coverage page."));

		await RunJobAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.Failed, walk.Status);
			Assert.Equal("Malformed coverage page.", walk.LastError);
			var inbox = (await MailboxSummaryDtoFactory.ListAsync(context, harness.Account.Id)).Single();
			Assert.Equal(MailboxAvailability.Degraded, inbox.Availability);
		});
		Assert.Single(
			await CreatedJobsAsync(harness),
			job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync)
		);
		Assert.False(await ClaimAsync(harness));
	}

	[Fact]
	public async Task A_walk_waiting_for_its_baseline_keeps_its_owner_and_tries_again_shortly()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		Assert.True(await ClaimAsync(harness));

		await RunJobAsync(harness);

		Assert.Equal(0, harness.Provider.AccountWalkPages);
		await harness.UsingAsync(async scope =>
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().AccountCoverageStates.ToListAsync())
		);
		Assert.Single(
			await CreatedJobsAsync(harness),
			job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync)
		);
		Assert.False(await ClaimAsync(harness));
	}

	private static async Task Prepare(SyncHarness harness)
	{
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
	}

	/// <summary>Takes the account's coverage owner key; false means someone already holds it.</summary>
	private static Task<bool> ClaimAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(
				scope
					.GetRequiredService<CoverageRegistry>()
					.TryStart(harness.Account.Id, CoverageRegistry.AccountWalkScope)
			)
		);

	private static Task RunJobAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			scope.GetRequiredService<SyncJobs>().AccountCoveragePageAsync(harness.Account.Id)
		);

	private static Task ChangeBoundAsync(SyncHarness harness, int bound) =>
		harness.UsingAsync(scope =>
			scope
				.GetRequiredService<MailHub>()
				.UpdateAccount(
					new AccountSettingsDto(
						harness.Account.Id,
						harness.Account.DisplayName,
						harness.Account.Color,
						harness.Account.PollIntervalSeconds,
						harness.Account.PollingEnabled,
						harness.Account.UndoSendDelaySeconds,
						harness.Account.NotificationsEnabled,
						InitialSyncMode.LastNMessages,
						bound,
						harness.Account.CertificateTrustMode,
						harness.Account.AttachmentSizeLimitOverride,
						AppendToSentOnSend: null,
						MaxMessageDownloadMegabytes: 128,
						GroupConversations: false
					)
				)
		);

	private static Task<List<Job>> CreatedJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created.ToList())
		);

	private static Task ClearJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
		{
			((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created.Clear();
			return Task.CompletedTask;
		});
}
