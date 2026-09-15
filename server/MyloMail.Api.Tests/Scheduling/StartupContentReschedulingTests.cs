using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
using MyloMail.Api.Tests.Outbox;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Hundred-and-eighty-fifth architecture-review pass: §6's startup-reconciliation table lists
/// <c>MessageContentState</c> in <c>Queued</c>/<c>Fetching</c> as a source of outstanding work
/// that startup "must enumerate," but <see cref="StartupScheduler.ScheduleAsync"/> never
/// enqueued <see cref="ContentJobs.FetchNextAsync"/> at all. That job only ever self-schedules
/// its own successor while content remains pending, and stops rescheduling the moment the queue
/// drains — so a message left <c>Queued</c>/<c>Fetching</c> by a crash had no path back onto the
/// queue except a live sync page happening to kick the chain again, which an account that is
/// fully caught up (or idling on IMAP IDLE) may not do for an arbitrarily long time.
/// </summary>
public sealed class StartupContentReschedulingTests
{
	[Fact]
	public async Task Startup_requeues_content_left_pending_by_a_crash()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = new Message
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				Subject = "Stuck mid-fetch",
				ReceivedAt = DateTimeOffset.UnixEpoch,
			};
			context.Messages.Add(message);
			context.MessageContentStates.Add(
				new MessageContentState
				{
					MessageId = message.Id,
					Status = ContentStatus.Fetching,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async scope =>
			await scope.GetRequiredService<StartupScheduler>().ScheduleAsync()
		);

		var created = await CreatedJobsAsync(harness);
		Assert.Contains(created, job => job.Method.Name == nameof(ContentJobs.FetchNextAsync));
	}
	[Fact]
	public async Task Startup_requeues_durable_staged_history_after_a_crash()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.StagedChangeEvents.Add(
				new StagedChangeEvent
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					Ordinal = 1,
					Payload = "{}",
					StagedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<StartupScheduler>().ScheduleAsync()
		);

		var created = await CreatedJobsAsync(harness);
		Assert.Contains(created, job => job.Method.Name == nameof(SyncJobs.ReplayStagedAsync));
	}

	[Fact]
	public async Task Reauthentication_requeues_staged_history_without_clearing_live_coverage_ownership()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		await SyncTests.ReconcileAsync(harness);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(candidate => candidate.Id == harness.Account.Id);
			account.AuthState = AuthState.NeedsReauth;
			await context.SaveChangesAsync();

			var mailboxId = await context.Mailboxes.Select(mailbox => mailbox.Id).FirstAsync();
			var coverage = scope.GetRequiredService<CoverageRegistry>();
			Assert.True(coverage.TryStart(account.Id, mailboxId));

			var recorder = (RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();
			await scope.GetRequiredService<StartupScheduler>().ResumeAccountAsync(account.Id);

			Assert.Contains(
				recorder.Created,
				job => job.Method.Name == nameof(SyncJobs.ReplayStagedAsync)
			);
			Assert.Contains(
				recorder.Created,
				job => job.Method.Name == nameof(ContentJobs.FetchNextAsync)
			);
			Assert.False(coverage.TryStart(account.Id, mailboxId));
		});
	}

	[Fact]
	public async Task Reauthentication_requeues_a_resumable_export()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var exportId = Guid.NewGuid();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.AuthState = AuthState.NeedsReauth;
			context.ExportJobs.Add(
				new ExportJob
				{
					Id = exportId,
					AccountId = account.Id,
					DestinationPath = Path.GetTempPath(),
					ManifestJson = "[]",
					Status = ExportJobStatus.Running,
				}
			);
			await context.SaveChangesAsync();

			var recorder =
				(RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();
			await scope.GetRequiredService<StartupScheduler>().ResumeAccountAsync(account.Id);

			Assert.Contains(
				recorder.Created,
				job =>
					job.Method.Name == nameof(ExportJobs.RunBatchAsync)
					&& (Guid)job.Args[0]! == exportId
			);
		});
	}

	[Fact]
	public async Task Startup_dispatches_durable_mutation_and_send_work()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(
					harness.AccountId,
					harness.MessageId,
					new FlagUpdate(IsRead: true, IsFlagged: null)
				)
		);
		await OutboxTests.SetUndoDelayAsync(harness, 0);
		await OutboxTests.QueueAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var recorder = (RecordingJobClient)services.GetRequiredService<Hangfire.IBackgroundJobClient>();
			recorder.Created.Clear();
			recorder.States.Clear();

			await services.GetRequiredService<StartupScheduler>().ScheduleAsync();

			Assert.Contains(
				recorder.Created,
				job => job.Method.Name == nameof(MutationJobs.DrainAsync)
			);
			Assert.Contains(
				recorder.Created,
				job => job.Method.Name == nameof(OutboxJobs.RunAsync)
			);
		});
	}


	private static Task<List<Job>> CreatedJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(
				((RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>()).Created
			)
		);
}
