using System.Net.Sockets;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Offline work is retained in memory because the database remains the durable source of
/// truth; recovery enqueues only the job needed to re-read that state.
/// </summary>
public sealed class ConnectivityPauseTests
{
	[Fact]
	public async Task Repeated_offline_requests_wait_and_resume_once_after_recovery()
	{
		var jobs = new RecordingJobClient();
		var events = new RecordingHubEvents();
		using var monitor = new ConnectivityMonitor(
			events,
			jobs,
			NullLogger<ConnectivityMonitor>.Instance
		);
		var accountId = Guid.NewGuid();
		var key = $"{nameof(ContentJobs)}:{accountId}";

		await monitor.ReportAsync(false);
		monitor.DispatchOrDefer(
			key,
			client => client.Enqueue<ContentJobs>(
				job => job.FetchNextAsync(accountId, default)
			)
		);
		monitor.DispatchOrDefer(
			key,
			client => client.Enqueue<ContentJobs>(
				job => job.FetchNextAsync(accountId, default)
			)
		);

		Assert.Empty(jobs.Created);
		Assert.Equal([false], events.Connectivity);

		await monitor.ReportAsync(true);

		Assert.Single(jobs.Created);
		Assert.Equal(nameof(ContentJobs.FetchNextAsync), jobs.Created[0].Method.Name);
		Assert.Equal([false, true], events.Connectivity);
	}

	[Fact]
	public async Task A_failed_recovery_enqueue_is_retained_for_the_next_successful_probe()
	{
		var jobs = new RecordingJobClient();
		using var monitor = new ConnectivityMonitor(
			new RecordingHubEvents(),
			jobs,
			NullLogger<ConnectivityMonitor>.Instance
		);
		var accountId = Guid.NewGuid();
		await monitor.PauseAsync(
			$"{nameof(ContentJobs)}:{accountId}",
			client => client.Enqueue<ContentJobs>(
				job => job.FetchNextAsync(accountId, default)
			)
		);
		jobs.CreateFailure = new InvalidOperationException("Hangfire unavailable.");

		await monitor.ReportAsync(true);
		Assert.Empty(jobs.Created);

		await monitor.ReportAsync(true);
		Assert.Single(jobs.Created);
	}

	[Fact]
	public async Task A_mutation_network_failure_does_not_enqueue_another_drain_until_recovery()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<MutationJobs>().DrainAsync(harness.AccountId)
		);
		await harness.UsingAsync(scope =>
		{
			((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>())
				.Created.Clear();
			return Task.CompletedTask;
		});
		harness.Clock.Advance(TimeSpan.FromSeconds(1));
		var failedMessageId = await MutationOrderingTests.AddMessageAsync(
			harness,
			"failed"
		);
		await harness.UsingAsync(scope =>
			scope
				.GetRequiredService<MutationQueue>()
				.MoveAsync(harness.AccountId, failedMessageId, harness.ArchiveId)
		);
		harness.Clock.Advance(TimeSpan.FromSeconds(1));
		var unattemptedMessageId = await MutationOrderingTests.AddMessageAsync(
			harness,
			"unattempted"
		);
		var unattempted = await harness.UsingAsync(scope =>
			scope
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(
					harness.AccountId,
					unattemptedMessageId,
					new FlagUpdate(null, true)
				)
		);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.MutationExecutionAttempts.AddRange(
				HistoricalAttempt(
					harness.Account.ProviderType,
					unattempted,
					MutationAttemptState.Completed
				),
				HistoricalAttempt(
					harness.Account.ProviderType,
					unattempted,
					MutationAttemptState.Prepared
				)
			);
			await context.SaveChangesAsync();
		});
		harness.Provider.FailNextBatchWith(new SocketException());

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<MutationJobs>().DrainAsync(harness.AccountId)
		);

		var beforeRecovery = await CreatedJobsAsync(harness);
		Assert.DoesNotContain(
			beforeRecovery,
			job => job.Method.Name == nameof(MutationJobs.DrainAsync)
		);
		await harness.UsingAsync(async scope =>
		{
			var items = await scope
				.GetRequiredService<MyloMailDbContext>()
				.MutationItems.ToDictionaryAsync(item => item.MessageId);
			Assert.Equal(MutationState.Completed, items[harness.MessageId].State);
			Assert.Equal(MutationState.Leased, items[failedMessageId].State);
			Assert.Equal(
				MutationState.Pending,
				items[unattemptedMessageId].State
			);
		});

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ConnectivityMonitor>().ReportAsync(true)
		);

		var afterRecovery = await CreatedJobsAsync(harness);
		Assert.Single(afterRecovery, job => job.Method.Name == nameof(MutationJobs.DrainAsync));

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<MutationJobs>().DrainAsync(harness.AccountId)
		);
		await harness.UsingAsync(async scope =>
		{
			var items = await scope
				.GetRequiredService<MyloMailDbContext>()
				.MutationItems.ToListAsync();
			Assert.All(items, item => Assert.Equal(MutationState.Completed, item.State));
		});
	}

	private static MutationExecutionAttempt HistoricalAttempt(
		ProviderType provider,
		MutationItem item,
		MutationAttemptState state
	) => new()
	{
		Id = Guid.NewGuid(),
		AccountId = item.AccountId,
		Provider = provider,
		OperationKind = item.OperationKind,
		State = state,
		CreatedAt = DateTimeOffset.UnixEpoch,
		DispatchedAt = state == MutationAttemptState.Prepared
			? null
			: DateTimeOffset.UnixEpoch,
		ResultPersistedAt = state == MutationAttemptState.Completed
			? DateTimeOffset.UnixEpoch
			: null,
		Items =
		[
			new MutationExecutionAttemptItem
			{
				MutationItemId = item.Id,
			},
		],
	};

	private static Task<List<Hangfire.Common.Job>> CreatedJobsAsync(MutationHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(
				((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created
			)
		);
}
