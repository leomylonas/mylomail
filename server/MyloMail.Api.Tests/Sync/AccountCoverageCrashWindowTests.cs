using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// Every cursor and page boundary of Gmail's account-wide walk (§16): the page and its resume
/// token, the walk's restart by a settings change or a triggered resynchronisation, a catch-up
/// page for a late label, and the failure record.
/// </summary>
/// <remarks>
/// <para>
/// The rule under test is the one the walk exists to keep: never persist a cursor past changes
/// that have not been durably persisted. Each walk here is deliberately multi-page and each
/// crash lands on a page whose token is not null — a single-page walk resumes from a null token
/// either way, so a token committed ahead of its data would look identical to one committed
/// with it, and the scenario would pass against the very bug it exists to catch.
/// </para>
/// <para>
/// <b>To prove a scenario discriminates, break the line it protects and watch it fail.</b> For
/// the page scenarios, in <c>CoverageService.RunWalkPageAsync</c> move
/// <c>walk.ResumeToken = page.HasMore ? page.ResumeToken : null;</c> and a
/// <c>SaveChangesAsync</c> ahead of <c>ingestor.IngestAsync(...)</c>, in a transaction of its
/// own: the token is then durable without the page, and the first three tests fail.
/// </para>
/// </remarks>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class AccountCoverageCrashWindowTests
{
	/// <summary>Kill point: the page was read from the provider, and neither it nor its token committed.</summary>
	[Fact]
	public async Task A_crash_before_the_walk_cursor_commits_replays_the_page()
	{
		await using var harness = await FiveMessagesAsync();

		harness.Faults.ArmAt(FaultPoints.SyncPageBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.CoverAsync(harness, pageSize: 2));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();

			// Neither the messages nor a position covering them survived. A token committed
			// ahead of its data would be "2" here, over an empty store.
			Assert.Empty(await context.Messages.ToListAsync());
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Null(walk.ResumeToken);
			Assert.Equal(0, walk.MessagesFetched);
		});

		await SyncTests.CoverAsync(harness, pageSize: 2);

		// Every message, including the first page's: the page the crash discarded is the page a
		// premature cursor would have skipped, and nothing would ever mention it again.
		await harness.UsingAsync(async scope =>
			Assert.Equal(5, await scope.GetRequiredService<MyloMailDbContext>().Messages.CountAsync())
		);
	}

	/// <summary>Kill point: the page is applied locally — messages, token, mirrored mailbox rows — but not committed.</summary>
	[Fact]
	public async Task A_crash_after_the_page_is_applied_but_before_it_commits_loses_all_of_it_together()
	{
		await using var harness = await FiveMessagesAsync();

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterApplyBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.CoverAsync(harness, pageSize: 2));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.Messages.ToListAsync());
			Assert.Empty(await context.MessageMailboxes.ToListAsync());
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Null(walk.ResumeToken);
			Assert.Equal(0, walk.MessagesFetched);

			// The mailbox rows that mirror the walk rolled back with it: nothing reports progress
			// the database does not hold.
			var inbox = await context.MailboxCoverageStates.SingleAsync();
			Assert.Equal(0, inbox.MessagesFetched);
		});

		await SyncTests.CoverAsync(harness, pageSize: 2);
		await harness.UsingAsync(async scope =>
			Assert.Equal(5, await scope.GetRequiredService<MyloMailDbContext>().Messages.CountAsync())
		);
	}

	/// <summary>
	/// Kill point: after a page and its cursor commit. The walk resumes from that cursor, so the
	/// committed page is not listed or fetched again — a cursor that did not commit would repeat it.
	/// </summary>
	[Fact]
	public async Task A_crash_after_the_cursor_commits_resumes_after_it_without_fetching_the_page_again()
	{
		await using var harness = await FiveMessagesAsync();

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.CoverAsync(harness, pageSize: 2));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal("2", walk.ResumeToken);
			Assert.Equal(2, walk.MessagesFetched);
			Assert.Equal(2, await context.Messages.CountAsync());
		});

		await SyncTests.CoverAsync(harness, pageSize: 2);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(5, await context.Messages.CountAsync());
			Assert.Equal(CoverageStatus.Covered, (await context.AccountCoverageStates.SingleAsync()).Status);
		});
		Assert.Equal(5, harness.Provider.AccountWalkMessageIds.Count);
		Assert.Equal(5, harness.Provider.AccountWalkMessageIds.Distinct().Count());
	}

	/// <summary>
	/// A settings change restarts the walk and every mailbox row in one transaction: a crash
	/// before it commits leaves the old policy, the old walk and the old rows — never a restarted
	/// walk beside a mailbox still claiming to be covered.
	/// </summary>
	[Fact]
	public async Task A_crash_while_a_settings_change_restarts_the_walk_leaves_the_old_policy_whole()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.AddMailbox("RECEIPTS");
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);
		var settings = Settings(harness.Account) with
		{
			InitialSyncMode = InitialSyncMode.LastNMessages,
			InitialSyncBoundValue = 10,
		};

		harness.Faults.ArmAt(FaultPoints.AccountCoveragePolicyAfterApplyBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(scope => scope.GetRequiredService<MailHub>().UpdateAccount(settings))
		);
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(InitialSyncMode.Full, account.InitialSyncMode);
			Assert.Equal(CoverageStatus.Covered, walk.Status);
			Assert.Equal(0, walk.PolicyGeneration);
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				row => Assert.Equal(CoverageStatus.Covered, row.Status)
			);
			Assert.All(
				await context.Mailboxes.ToListAsync(),
				mailbox => Assert.Equal(0, mailbox.CoveragePolicyGeneration)
			);
		});

		await harness.UsingAsync(scope => scope.GetRequiredService<MailHub>().UpdateAccount(settings));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
			Assert.Equal(1, walk.PolicyGeneration);
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				row => Assert.Equal(CoverageStatus.NotStarted, row.Status)
			);
			var jobs = ((RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>())
				.Created.Where(job => job.Method.Name == nameof(SyncJobs.AccountCoveragePageAsync))
				.ToList();
			Assert.Single(jobs);
		});
	}

	/// <summary>
	/// A triggered resynchronisation resets the cursor, the walk, its generation and every mailbox
	/// row together. Crashing before the commit must leave the established baseline and the walk's
	/// position exactly as they were.
	/// </summary>
	[Fact]
	public async Task A_crash_during_a_triggered_resync_leaves_the_walk_where_it_was()
	{
		await using var harness = await FiveMessagesAsync();
		Assert.True(await RunPageAsync(harness, pageSize: 2));
		harness.Provider.InvalidateCursors();
		harness.Faults.ArmAt(FaultPoints.CursorInvalidationAfterApplyBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.SyncAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var stream = await context.ChangeStreamStates.SingleAsync();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.NotNull(stream.CursorState);
			Assert.False(stream.IsRebasing);
			Assert.Equal(CoverageStatus.Backfilling, walk.Status);
			Assert.Equal("2", walk.ResumeToken);
			Assert.Equal(2, walk.MessagesFetched);
			Assert.Equal(0, walk.PolicyGeneration);
		});

		var outcome = await SyncTests.SyncAsync(harness);

		Assert.True(outcome.ResyncTriggered);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.True((await context.ChangeStreamStates.SingleAsync()).IsRebasing);
			Assert.Equal(CoverageStatus.NotStarted, walk.Status);
			Assert.Null(walk.ResumeToken);
			Assert.Equal(0, walk.MessagesFetched);
			Assert.Equal(1, walk.PolicyGeneration);
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				row => Assert.Equal(CoverageStatus.NotStarted, row.Status)
			);
		});
	}

	/// <summary>
	/// Catch-up for a label created after the walk is an ordinary per-mailbox page, with the same
	/// boundary: a crash before its commit leaves the label uncovered and its membership unwritten.
	/// </summary>
	[Fact]
	public async Task A_crash_during_a_catch_up_page_leaves_the_label_uncovered_until_it_replays()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);
		harness.Provider.AddMailbox("FRESH");
		harness.Provider.SeedMessage("FRESH", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddDays(1));
		await SyncTests.ReconcileAsync(harness);

		harness.Faults.ArmAt(FaultPoints.SyncPageAfterApplyBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SyncTests.CoverAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var fresh = await harness.MailboxAsync(scope, "FRESH");
			Assert.Equal(0, await context.MessageMailboxes.CountAsync(o => o.MailboxId == fresh.Id));
			Assert.NotEqual(
				CoverageStatus.Covered,
				(await context.MailboxCoverageStates.SingleOrDefaultAsync(c => c.MailboxId == fresh.Id))?.Status
			);
		});

		await SyncTests.CoverAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var fresh = await harness.MailboxAsync(scope, "FRESH");
			Assert.Equal(1, await context.MessageMailboxes.CountAsync(o => o.MailboxId == fresh.Id));
			Assert.Equal(
				CoverageStatus.Covered,
				(await context.MailboxCoverageStates.SingleAsync(c => c.MailboxId == fresh.Id)).Status
			);
		});
	}

	/// <summary>
	/// Recording a failure is itself a commit boundary: a crash before it leaves the walk as it
	/// was, so a restart resumes from the same cursor rather than from a half-recorded failure.
	/// </summary>
	[Fact]
	public async Task A_crash_while_recording_a_walk_failure_leaves_the_previous_state_durable()
	{
		await using var harness = await FiveMessagesAsync();
		Assert.True(await RunPageAsync(harness, pageSize: 2));
		harness.Provider.BeforeInitialSyncReturnAsync = () =>
			Task.FromException(new InvalidOperationException("Malformed coverage page."));
		harness.Faults.ArmAt(FaultPoints.MailboxHealthAfterApplyBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(async scope =>
			{
				var coverage = scope.GetRequiredService<CoverageService>();
				var account = await harness.AccountInScopeAsync(scope);
				var fence = await coverage.CaptureFenceAsync(account);
				var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
					coverage.RunAccountPageAsync(account, 2)
				);
				await coverage.RecordAccountFailureAsync(account.Id, fence, failure);
			})
		);
		await harness.RestartAsync();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var walk = await context.AccountCoverageStates.SingleAsync();
			Assert.Equal(CoverageStatus.Backfilling, walk.Status);
			Assert.Null(walk.LastError);
			Assert.Equal("2", walk.ResumeToken);
			Assert.All(
				await context.MailboxCoverageStates.ToListAsync(),
				row => Assert.Equal(CoverageStatus.Backfilling, row.Status)
			);
		});

		harness.Provider.BeforeInitialSyncReturnAsync = null;
		await SyncTests.CoverAsync(harness, pageSize: 2);
		await harness.UsingAsync(async scope =>
			Assert.Equal(5, await scope.GetRequiredService<MyloMailDbContext>().Messages.CountAsync())
		);
	}

	private static async Task<SyncHarness> FiveMessagesAsync()
	{
		var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		for (var index = 0; index < 5; index++)
		{
			harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch.AddMinutes(index));
		}
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		return harness;
	}

	private static Task<bool> RunPageAsync(SyncHarness harness, int pageSize) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<CoverageService>()
				.RunAccountPageAsync(await harness.AccountInScopeAsync(scope), pageSize)
		);

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
			AppendToSentOnSend: null,
			MaxMessageDownloadMegabytes: 128,
			GroupConversations: false
		);
}
