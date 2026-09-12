using System.Net.Sockets;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MimeKit.Text;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

public sealed class ContentAcquisitionTopologyGenerationTests
{
	[Fact]
	public async Task Fetch_is_discarded_when_its_mailbox_generation_changes_in_flight()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailboxId, messageId) = await SeedAsync(harness);
		var enteredProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		harness.Provider.BeforeFetchRawMessageReturnAsync = async _ =>
		{
			enteredProvider.SetResult();
			await releaseProvider.Task;
		};

		var acquisition = harness.UsingAsync(async scope =>
		{
			var account = await harness.AccountInScopeAsync(scope);
			await scope.GetRequiredService<ContentAcquisition>().AcquireAsync(account, messageId);
		});
		await enteredProvider.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.SingleAsync(m => m.Id == mailboxId);
			mailbox.TopologyGeneration++;
			await context.SaveChangesAsync();
		});
		releaseProvider.SetResult();
		await acquisition;

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync(m => m.Id == messageId);
			var state = await context.MessageContentStates.SingleAsync(s => s.MessageId == messageId);
			Assert.False(message.RawFetched);
			Assert.False(await context.MessageRaws.AnyAsync(r => r.MessageId == messageId));
			Assert.Equal(ContentStatus.Queued, state.Status);
			Assert.Equal(0, state.Attempts);
		});
	}

	[Fact]
	public async Task Fetch_is_discarded_when_its_exact_occurrence_changes_in_flight()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (_, messageId) = await SeedAsync(harness);
		var enteredProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		harness.Provider.BeforeFetchRawMessageReturnAsync = async _ =>
		{
			enteredProvider.SetResult();
			await releaseProvider.Task;
		};

		var acquisition = harness.UsingAsync(async scope =>
		{
			var account = await harness.AccountInScopeAsync(scope);
			await scope.GetRequiredService<ContentAcquisition>().AcquireAsync(account, messageId);
		});
		await enteredProvider.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync(o => o.MessageId == messageId);
			occurrence.ProviderOccurrenceId = "replacement-occurrence";
			await context.SaveChangesAsync();
		});
		releaseProvider.SetResult();
		await acquisition;

		await AssertDiscardedAsync(harness, messageId);
	}

	[Fact]
	public async Task Failure_from_a_stale_fetch_does_not_consume_the_replacement_retry_budget()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailboxId, messageId) = await SeedAsync(harness);
		harness.Provider.BeforeFetchRawMessageReturnAsync = async _ =>
		{
			await harness.UsingAsync(async scope =>
			{
				var context = scope.GetRequiredService<MyloMailDbContext>();
				var mailbox = await context.Mailboxes.SingleAsync(m => m.Id == mailboxId);
				mailbox.TopologyGeneration++;
				await context.SaveChangesAsync();
			});
			throw new InvalidOperationException("Old occurrence disappeared.");
		};

		var result = await harness.UsingAsync(async scope =>
		{
			var account = await harness.AccountInScopeAsync(scope);
			return await scope
				.GetRequiredService<ContentAcquisition>()
				.AcquireAsync(account, messageId);
		});
		Assert.Equal(ContentAcquisitionResult.Deferred, result);

		await AssertDiscardedAsync(harness, messageId);
	}

	[Fact]
	public async Task Crash_before_content_commit_rolls_back_and_restart_refetches()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (_, messageId) = await SeedAsync(harness);
		harness.Faults.ArmAt(FaultPoints.ContentAfterApplyBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(scope =>
				scope.GetRequiredService<ContentJobs>().FetchNextAsync(harness.Account.Id)
			)
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.False(await context.MessageRaws.AnyAsync(r => r.MessageId == messageId));
			var state = await context.MessageContentStates.SingleAsync(s => s.MessageId == messageId);
			Assert.Equal(ContentStatus.Fetching, state.Status);
		});

		await harness.RestartAsync();
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ContentJobs>().FetchNextAsync(harness.Account.Id)
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.MessageRaws.AnyAsync(r => r.MessageId == messageId));
			var state = await context.MessageContentStates.SingleAsync(s => s.MessageId == messageId);
			Assert.Equal(ContentStatus.Indexed, state.Status);
		});
	}

	[Fact]
	public async Task A_content_network_failure_waits_for_connectivity_before_enqueuing_another_fetch()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (_, messageId) = await SeedAsync(harness);
		harness.Provider.FailFetchRawMessageWith(new SocketException());

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ContentJobs>().FetchNextAsync(harness.Account.Id)
		);

		var beforeRecovery = await CreatedJobsAsync(harness);
		Assert.DoesNotContain(
			beforeRecovery,
			job => job.Method.Name == nameof(ContentJobs.FetchNextAsync)
		);
		await harness.UsingAsync(async scope =>
		{
			var state = await scope
				.GetRequiredService<MyloMailDbContext>()
				.MessageContentStates.SingleAsync(item => item.MessageId == messageId);
			Assert.Equal(ContentStatus.Queued, state.Status);
			Assert.Equal(0, state.Attempts);
		});
		await Assert.ThrowsAsync<HttpRequestException>(() =>
			harness.UsingAsync(async scope =>
			{
				var account = await harness.AccountInScopeAsync(scope);
				await scope
					.GetRequiredService<ContentAcquisition>()
					.AcquireAsync(account, messageId);
			})
		);

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<ConnectivityMonitor>().ReportAsync(true)
		);

		var afterRecovery = await CreatedJobsAsync(harness);
		Assert.Single(
			afterRecovery,
			job => job.Method.Name == nameof(ContentJobs.FetchNextAsync)
		);
	}

	private static Task<List<Hangfire.Common.Job>> CreatedJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(
				((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created
			)
		);

	private static Task AssertDiscardedAsync(SyncHarness harness, Guid messageId) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync(m => m.Id == messageId);
			var state = await context.MessageContentStates.SingleAsync(s => s.MessageId == messageId);
			Assert.False(message.RawFetched);
			Assert.False(await context.MessageRaws.AnyAsync(r => r.MessageId == messageId));
			Assert.Equal(ContentStatus.Queued, state.Status);
			Assert.Equal(0, state.Attempts);
		});

	private static async Task<(Guid MailboxId, Guid MessageId)> SeedAsync(SyncHarness harness)
	{
		var providerMailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var messageId = Guid.NewGuid();
		var providerOccurrenceId = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		providerMailbox.Messages[providerOccurrenceId].RawBytes = MimeBytes();
		var mailboxId = Guid.NewGuid();

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = harness.Account.Id,
					ProviderMailboxId = "INBOX",
					Name = "Inbox",
					SpecialUse = SpecialUse.Inbox,
				}
			);
			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.Account.Id,
					ReceivedAt = DateTimeOffset.UnixEpoch,
					Occurrences =
					[
						new MessageMailbox
						{
							Id = Guid.NewGuid(),
							MailboxId = mailboxId,
							ProviderOccurrenceId = providerOccurrenceId,
						},
					],
				}
			);
			context.MessageContentStates.Add(
				new MessageContentState
				{
					MessageId = messageId,
					Status = ContentStatus.Queued,
				}
			);
			await context.SaveChangesAsync();
		});

		return (mailboxId, messageId);
	}

	private static byte[] MimeBytes()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.To.Add(MailboxAddress.Parse("recipient@example.test"));
		message.Subject = "Body";
		message.Body = new TextPart("plain") { Text = "Current content" };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
