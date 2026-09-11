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
/// Hundred-and-twenty-first architecture-review pass: <c>TopologySyncService</c>'s own doc
/// comment says each provider "reconciles topology separately and on its own cadence," but
/// <see cref="SyncJobs.TopologyAsync"/> only ever ran once, at startup or account resume, and
/// never rescheduled itself — unlike every sibling loop (change streams, integrity
/// reconciliation, calendar sync). A folder created, renamed or deleted in another client mid-
/// session was invisible until the app next restarted.
/// </summary>
/// <remarks>
/// A first version of this fix put the <see cref="PollRegistry"/> claim guard inside
/// <see cref="SyncJobs.TopologyAsync"/> itself — the same method that reschedules itself on
/// success. That combination deadlocks: the claim taken on entry is never released before the
/// self-reschedule, so the scheduled successor's own call finds the scope already held and
/// returns immediately, enqueuing nothing further. The loop looked like it was rescheduling
/// (one successor job really was created) but died silently after that single cycle. Caught by
/// <c>invariant-review</c> before this shipped; fixed by moving the claim to
/// <see cref="StartupScheduler"/>'s two call sites (mirroring how <c>StartCalendarLoop</c>
/// claims its own scope), leaving <c>TopologyAsync</c>'s own self-reschedule unguarded — the
/// second test below exercises the scheduled successor itself, not just the first call, since
/// that is exactly the case the deadlocking version passed.
/// </remarks>
public sealed class TopologyReschedulingTests
{
	[Fact]
	public async Task A_topology_run_reschedules_itself()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		var created = await CreatedJobsAsync(harness);
		Assert.Contains(created, job => job.Method.Name == nameof(SyncJobs.TopologyAsync));
	}

	[Fact]
	public async Task Gmail_synthetic_intermediates_never_receive_provider_sync_jobs()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("Projects/Client");

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		var created = await CreatedJobsAsync(harness);
		Assert.Single(created, job => job.Method.Name == nameof(SyncJobs.CoveragePageAsync));

		var syntheticId = await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<MyloMailDbContext>()
				.Mailboxes.Where(mailbox => mailbox.ProviderMailboxId == null)
				.Select(mailbox => mailbox.Id)
				.SingleAsync()
		);
		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>()
				.CoveragePageAsync(harness.Account.Id, syntheticId)
		);
		await harness.UsingAsync(async scope =>
			Assert.False(
				await scope.GetRequiredService<MyloMailDbContext>()
					.MailboxCoverageStates.AnyAsync(state => state.MailboxId == syntheticId)
			)
		);
	}

	[Fact]
	public async Task Graph_change_stream_waits_for_coverage_then_bootstraps_its_delta()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);
		Assert.DoesNotContain(
			await CreatedJobsAsync(harness),
			job => job.Method.Name == nameof(SyncJobs.ChangeStreamAsync)
		);

		var mailboxId = await harness.UsingAsync(async scope =>
			(await harness.MailboxAsync(scope, "INBOX")).Id
		);
		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>()
				.CoveragePageAsync(harness.Account.Id, mailboxId)
		);

		Assert.Contains(
			await CreatedJobsAsync(harness),
			job => job.Method.Name == nameof(SyncJobs.ChangeStreamAsync)
		);
	}

	[Fact]
	public async Task Established_Graph_stream_continues_while_a_new_coverage_policy_backfills()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.CoverAsync(harness);
		await SyncTests.SyncAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var coverage = await context.MailboxCoverageStates.SingleAsync();
			coverage.Status = CoverageStatus.NotStarted;
			await context.SaveChangesAsync();
			await scope.GetRequiredService<SyncJobs>()
				.StartChangeStreamsAsync(await harness.AccountInScopeAsync(scope));
		});

		Assert.Contains(
			await CreatedJobsAsync(harness),
			job => job.Method.Name == nameof(SyncJobs.ChangeStreamAsync)
		);
	}

	/// <summary>
	/// Runs <see cref="SyncJobs.TopologyAsync"/> twice in a row — the second call standing in
	/// for Hangfire actually firing the job the first call scheduled — and asserts the second
	/// run reschedules too. The deadlocking version of this fix passed a version of this test
	/// that stopped after the first call; only actually invoking the scheduled successor proves
	/// the loop survives past one cycle.
	/// </summary>
	[Fact]
	public async Task Its_own_scheduled_successor_reconciles_and_reschedules_again()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);
		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
		);

		var created = await CreatedJobsAsync(harness);
		Assert.Equal(2, created.Count(job => job.Method.Name == nameof(SyncJobs.TopologyAsync)));
	}

	/// <summary>
	/// A third caller of <see cref="SyncJobs.TopologyAsync"/> — <see cref="MailHub.UpdateAccount"/>
	/// re-enqueuing it when polling is re-enabled — was missed when the claim guard moved from
	/// inside <c>TopologyAsync</c> to its callers: its own surrounding comment described a
	/// <c>TryStart</c> call that the code never actually made. Since <c>TopologyAsync</c> now
	/// self-reschedules forever on success, every disable/re-enable toggle through account
	/// settings would have started an additional, permanently self-perpetuating topology loop
	/// with no dedup against one already running. Caught by a second <c>invariant-review</c>
	/// pass; fixed by adding the same <c>TryStart</c> guard used at the other two call sites.
	/// </summary>
	[Fact]
	public async Task Re_enabling_polling_through_account_settings_does_not_start_a_second_loop()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.PollingEnabled = false;
			await context.SaveChangesAsync();

			// A loop still genuinely alive across the disable — it hasn't ticked yet, so it
			// never released its own slot.
			scope.GetRequiredService<PollRegistry>().TryStart(harness.Account.Id, SyncJobs.TopologyScope);
		});

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<MailHub>().UpdateAccount(AccountSettings(harness.Account, pollingEnabled: true))
		);

		// The still-alive loop's claim is respected: re-enabling polling did not enqueue a
		// second, concurrent topology loop for the same account.
		var created = await CreatedJobsAsync(harness);
		Assert.DoesNotContain(created, job => job.Method.Name == nameof(SyncJobs.TopologyAsync));
	}

	/// <summary>
	/// Hundred-and-forty-fifth pass: § Offline behaviour promises network-class failures
	/// "suppress normal per-job retry noise/logging until connectivity returns, rather than
	/// surfacing every offline poll attempt as a fresh failure" — but nothing actually
	/// distinguished a network failure from a genuine bug here; both hit the same
	/// <c>catch (Exception) { polls.Stop(...); throw; }</c>, ending the loop and letting
	/// Hangfire log a job failure on every single offline poll. A network-class failure must
	/// now reschedule quietly instead, keeping the loop's claim on <see cref="PollRegistry"/>
	/// so it resumes on its own once connectivity returns.
	/// </summary>
	[Fact]
	public async Task A_network_class_failure_reschedules_without_releasing_the_loops_claim()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.FailListMailboxesWith(new System.Net.Sockets.SocketException());

		await harness.UsingAsync(async scope =>
		{
			scope.GetRequiredService<PollRegistry>().TryStart(harness.Account.Id, SyncJobs.TopologyScope);
			await scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id);
		});

		var created = await CreatedJobsAsync(harness);
		Assert.Contains(created, job => job.Method.Name == nameof(SyncJobs.TopologyAsync));

		// Still claimed: a second TryStart for the same scope must fail, proving polls.Stop
		// was never called for this failure.
		var stillClaimed = await harness.UsingAsync(scope =>
			Task.FromResult(!scope.GetRequiredService<PollRegistry>().TryStart(harness.Account.Id, SyncJobs.TopologyScope))
		);
		Assert.True(stillClaimed);
	}

	/// <summary>
	/// The contrasting case: a genuine bug (not network-class) must still stop the loop and
	/// propagate, exactly as before this pass — the new catch clause's <c>when</c> guard must
	/// not accidentally swallow real application errors along with network ones.
	/// </summary>
	[Fact]
	public async Task A_non_network_failure_still_stops_the_loop_and_propagates()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.FailListMailboxesWith(new InvalidOperationException("not a network problem"));

		await harness.UsingAsync(async scope =>
		{
			scope.GetRequiredService<PollRegistry>().TryStart(harness.Account.Id, SyncJobs.TopologyScope);
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => scope.GetRequiredService<SyncJobs>().TopologyAsync(harness.Account.Id)
			);
		});

		// Released: a fresh TryStart for the same scope must succeed, proving polls.Stop ran.
		var released = await harness.UsingAsync(scope =>
			Task.FromResult(scope.GetRequiredService<PollRegistry>().TryStart(harness.Account.Id, SyncJobs.TopologyScope))
		);
		Assert.True(released);
	}

	private static AccountSettingsDto AccountSettings(Account account, bool pollingEnabled) =>
		new(
			account.Id,
			account.DisplayName,
			account.Color,
			account.PollIntervalSeconds,
			pollingEnabled,
			account.UndoSendDelaySeconds,
			account.NotificationsEnabled,
			account.CertificateTrustMode,
			account.AttachmentSizeLimitOverride,
			null
		);

	private static Task<List<Job>> CreatedJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created)
		);
}
