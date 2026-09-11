using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

public sealed class MailboxAvailabilityProjectionTests
{
	[Fact]
	public async Task Availability_remains_orthogonal_to_coverage_and_sync_health()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);

		var initial = await SummaryAsync(harness);
		Assert.Equal(CoverageStatus.NotStarted, initial.Coverage);
		Assert.Equal(MailboxAvailability.Unavailable, initial.Availability);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.SingleAsync();
			context.MailboxCoverageStates.Add(
				new MailboxCoverageState
				{
					MailboxId = mailbox.Id,
					Status = CoverageStatus.Backfilling,
				}
			);
			await context.SaveChangesAsync();
		});

		var backfilling = await SummaryAsync(harness);
		Assert.Equal(CoverageStatus.Backfilling, backfilling.Coverage);
		Assert.Equal(MailboxAvailability.Usable, backfilling.Availability);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var coverage = await context.MailboxCoverageStates.SingleAsync();
			coverage.Status = CoverageStatus.Failed;
			await context.SaveChangesAsync();
		});
		Assert.Equal(MailboxAvailability.Degraded, (await SummaryAsync(harness)).Availability);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.SingleAsync();
			var coverage = await context.MailboxCoverageStates.SingleAsync();
			coverage.Status = CoverageStatus.Covered;
			context.ChangeStreamStates.Add(
				new ChangeStreamState
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					MailboxId = mailbox.Id,
					IsRebasing = true,
				}
			);
			await context.SaveChangesAsync();
		});
		var rebasing = await SummaryAsync(harness);
		Assert.Equal(CoverageStatus.Covered, rebasing.Coverage);
		Assert.Equal(MailboxAvailability.Degraded, rebasing.Availability);
	}

	[Fact]
	public async Task Coverage_terminal_failure_is_persisted_and_announced_as_degraded()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		harness.Provider.BeforeInitialSyncReturnAsync = () =>
			Task.FromException(new InvalidOperationException("Malformed coverage page."));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.CoveragePageAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);

		var summary = await SummaryAsync(harness);
		Assert.Equal(CoverageStatus.Failed, summary.Coverage);
		Assert.Equal(MailboxAvailability.Degraded, summary.Availability);
		Assert.Equal(MailboxAvailability.Degraded, harness.Events.Mailboxes.Last().Availability);
		harness.Provider.BeforeInitialSyncReturnAsync = null;
		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<SyncJobs>()
				.CoveragePageAsync(
					harness.Account.Id,
					(await harness.MailboxAsync(scope, "INBOX")).Id
				)
		);
		Assert.Equal(MailboxAvailability.Usable, harness.Events.Mailboxes.Last().Availability);
	}

	[Fact]
	public async Task Crash_before_health_commit_leaves_the_previous_availability_durable()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await SetCoverageAsync(harness, CoverageStatus.Backfilling);
		harness.Provider.BeforeInitialSyncReturnAsync = () =>
			Task.FromException(new InvalidOperationException("Malformed coverage page."));
		harness.Events.Mailboxes.Clear();
		harness.Faults.ArmAt(FaultPoints.MailboxHealthAfterApplyBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.CoveragePageAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);
		await harness.RestartAsync();

		var afterRestart = await SummaryAsync(harness);
		Assert.Equal(CoverageStatus.Backfilling, afterRestart.Coverage);
		Assert.Equal(MailboxAvailability.Usable, afterRestart.Availability);
		Assert.Empty(harness.Events.Mailboxes);

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.CoveragePageAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);
		Assert.Equal(MailboxAvailability.Degraded, (await SummaryAsync(harness)).Availability);
		Assert.Equal(MailboxAvailability.Degraded, Assert.Single(harness.Events.Mailboxes).Availability);
	}

	[Fact]
	public async Task Topology_terminal_failure_is_persisted_and_announced_as_degraded()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await SetCoverageAsync(harness, CoverageStatus.Backfilling);
		harness.Provider.FailListMailboxesWith(new InvalidOperationException("Malformed topology."));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(scope =>
				scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
			)
		);

		var summary = await SummaryAsync(harness);
		Assert.Equal(MailboxAvailability.Degraded, summary.Availability);
		Assert.Equal("Malformed topology.", await harness.UsingAsync(async scope =>
			(await scope
				.GetRequiredService<MyloMailDbContext>()
				.MailboxTopologySyncStates.SingleAsync()).LastError
		));
		Assert.Equal(MailboxAvailability.Degraded, Assert.Single(harness.Events.Mailboxes).Availability);
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);
		Assert.Equal(MailboxAvailability.Usable, harness.Events.Mailboxes.Last().Availability);
	}

	[Fact]
	public async Task Integrity_terminal_failure_is_persisted_and_announced_as_degraded()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await SetCoverageAsync(harness, CoverageStatus.Backfilling);
		harness.Provider.FailNextIntegrityWith(new InvalidOperationException("Malformed UID snapshot."));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.IntegrityAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);

		var summary = await SummaryAsync(harness);
		Assert.Equal(MailboxAvailability.Degraded, summary.Availability);
		Assert.Equal("Malformed UID snapshot.", await harness.UsingAsync(async scope =>
			(await scope
				.GetRequiredService<MyloMailDbContext>()
				.IntegrityReconciliationStates.SingleAsync()).LastError
		));
		Assert.Equal(MailboxAvailability.Degraded, Assert.Single(harness.Events.Mailboxes).Availability);
		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<SyncJobs>()
				.IntegrityAsync(
					harness.Account.Id,
					(await harness.MailboxAsync(scope, "INBOX")).Id
				)
		);
		Assert.Equal(MailboxAvailability.Usable, harness.Events.Mailboxes.Last().Availability);
	}

	[Fact]
	public async Task Change_stream_terminal_failure_and_recovery_are_persisted_and_announced()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await SetCoverageAsync(harness, CoverageStatus.Covered);
		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.SyncAsync(
					await harness.AccountInScopeAsync(scope),
					await harness.MailboxAsync(scope, "INBOX")
				)
		);
		harness.Events.Mailboxes.Clear();
		harness.Provider.FailNextChangeStreamWith(new InvalidOperationException("Malformed delta."));

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.ChangeStreamAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);
		Assert.Equal("Malformed delta.", await harness.UsingAsync(async scope =>
			(await scope
				.GetRequiredService<MyloMailDbContext>()
				.ChangeStreamStates.SingleAsync()).LastError
		));
		Assert.Equal(MailboxAvailability.Degraded, Assert.Single(harness.Events.Mailboxes).Availability);

		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<SyncJobs>()
				.ChangeStreamAsync(
					harness.Account.Id,
					(await harness.MailboxAsync(scope, "INBOX")).Id
				)
		);
		Assert.Equal(MailboxAvailability.Usable, harness.Events.Mailboxes.Last().Availability);
	}

	[Fact]
	public async Task Stale_mailbox_stream_failure_cannot_degrade_a_new_topology_generation()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await SetCoverageAsync(harness, CoverageStatus.Covered);
		await harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ChangeStreamService>()
				.SyncAsync(
					await harness.AccountInScopeAsync(scope),
					await harness.MailboxAsync(scope, "INBOX")
				)
		);
		harness.Events.Mailboxes.Clear();
		harness.Provider.BeforeChangeStreamReturnAsync = () =>
			harness.UsingAsync(async scope =>
			{
				var context = scope.GetRequiredService<MyloMailDbContext>();
				var mailbox = await harness.MailboxAsync(scope, "INBOX");
				mailbox.TopologyGeneration++;
				await context.SaveChangesAsync();
				throw new InvalidOperationException("Stale malformed delta.");
			});

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.ChangeStreamAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);

		Assert.Null(await harness.UsingAsync(async scope =>
			(await scope.GetRequiredService<MyloMailDbContext>().ChangeStreamStates.SingleAsync()).LastError
		));
		Assert.Equal(MailboxAvailability.Usable, (await SummaryAsync(harness)).Availability);
		Assert.Empty(harness.Events.Mailboxes);
	}

	[Fact]
	public async Task Stale_integrity_failure_cannot_degrade_a_new_topology_generation()
	{
		await using var harness = await SyncHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await ReconcileAsync(harness);
		await SetCoverageAsync(harness, CoverageStatus.Backfilling);
		harness.Events.Mailboxes.Clear();
		harness.Provider.BeforeIntegritySnapshotReturnAsync = () =>
			harness.UsingAsync(async scope =>
			{
				var context = scope.GetRequiredService<MyloMailDbContext>();
				var mailbox = await harness.MailboxAsync(scope, "INBOX");
				mailbox.TopologyGeneration++;
				await context.SaveChangesAsync();
				throw new InvalidOperationException("Stale malformed snapshot.");
			});

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(async scope =>
				await scope
					.GetRequiredService<SyncJobs>()
					.IntegrityAsync(
						harness.Account.Id,
						(await harness.MailboxAsync(scope, "INBOX")).Id
					)
			)
		);

		Assert.Empty(await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().IntegrityReconciliationStates.ToListAsync()
		));
		Assert.Equal(MailboxAvailability.Usable, (await SummaryAsync(harness)).Availability);
		Assert.Empty(harness.Events.Mailboxes);
	}

	[Fact]
	public async Task Gmail_baseline_failure_is_recorded_on_the_account_stream_not_topology()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.BeforeChangeStreamReturnAsync = () =>
			harness.UsingAsync(async scope =>
			{
				var context = scope.GetRequiredService<MyloMailDbContext>();
				var mailbox = await context.Mailboxes.SingleAsync();
				mailbox.TopologyGeneration++;
				await context.SaveChangesAsync();
				throw new InvalidOperationException("Malformed history.");
			});

		await Assert.ThrowsAsync<InvalidOperationException>(() =>
			harness.UsingAsync(scope =>
				scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
			)
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal("Malformed history.", (await context.ChangeStreamStates.SingleAsync()).LastError);
			Assert.Null((await context.MailboxTopologySyncStates.SingleAsync()).LastError);
		});
	}

	[Fact]
	public async Task Synthetic_hierarchy_nodes_are_usable_without_fake_coverage()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Projects/Client");
		await ReconcileAsync(harness);

		var summaries = await harness.UsingAsync(scope =>
			MailboxSummaryDtoFactory.ListAsync(
				scope.GetRequiredService<MyloMailDbContext>(),
				harness.Account.Id
			)
		);
		var synthetic = Assert.Single(summaries, mailbox => mailbox.IsSynthesized);
		var providerBacked = Assert.Single(summaries, mailbox => !mailbox.IsSynthesized);
		Assert.Equal(MailboxAvailability.Usable, synthetic.Availability);
		Assert.Equal(CoverageStatus.NotStarted, synthetic.Coverage);
		Assert.Equal(MailboxAvailability.Unavailable, providerBacked.Availability);
	}

	private static async Task<MyloMail.Api.Contracts.MailboxSummaryDto> SummaryAsync(
		SyncHarness harness
	) =>
		await harness.UsingAsync(async scope =>
			await MailboxSummaryDtoFactory.GetAsync(
				scope.GetRequiredService<MyloMailDbContext>(),
				(await harness.MailboxAsync(scope, "INBOX")).Id
			)
			?? throw new InvalidOperationException("Mailbox summary was not projected.")
		);

	private static Task SetCoverageAsync(SyncHarness harness, CoverageStatus status) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await harness.MailboxAsync(scope, "INBOX");
			context.MailboxCoverageStates.Add(
				new MailboxCoverageState
				{
					MailboxId = mailbox.Id,
					Status = status,
				}
			);
			await context.SaveChangesAsync();
		});

	private static Task ReconcileAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<TopologySyncService>()
				.ReconcileAsync(await harness.AccountInScopeAsync(scope))
		);
}
