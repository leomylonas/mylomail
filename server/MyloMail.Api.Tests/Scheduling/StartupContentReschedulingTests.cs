using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
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

	private static Task<List<Job>> CreatedJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(
				((RecordingJobClient)scope.GetRequiredService<Hangfire.IBackgroundJobClient>()).Created
			)
		);
}
