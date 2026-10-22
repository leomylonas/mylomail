using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

public sealed class BackgroundTasksTests
{
	[Fact]
	public async Task A_queued_change_counts_until_the_provider_has_confirmed_it()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var messageId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(new Message { Id = messageId, AccountId = harness.Account.Id, IsRead = false });
			await context.SaveChangesAsync();
		});
		Task<int> CountAsync() =>
			harness.UsingAsync(async services =>
				(await services.GetRequiredService<MailHub>().GetBackgroundTasks()).Count(task => task.Label.StartsWith("Updating") && !task.Running));

		Assert.Equal(0, await CountAsync());
		await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().SetFlags(harness.Account.Id, [messageId], true, null));
		Assert.Equal(1, await CountAsync());

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			(await context.MutationItems.SingleAsync()).State = MutationState.Completed;
			await context.SaveChangesAsync();
		});
		Assert.Equal(0, await CountAsync());
	}

	[Fact]
	public async Task Mail_in_the_outbox_is_listed_while_it_waits_and_is_gone_once_sent()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var now = DateTimeOffset.UtcNow;
		OutboxItem Item(OutboxStatus status, TimeSpan due) =>
			new()
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				DraftId = Guid.NewGuid(),
				Status = status,
				ScheduledSendAt = now + due,
				CreatedAt = now,
				StableMessageId = $"<{Guid.NewGuid():N}@example.test>",
			};
		var waiting = Item(OutboxStatus.Scheduled, TimeSpan.FromSeconds(10));
		var later = Item(OutboxStatus.Scheduled, TimeSpan.FromHours(20));
		var sent = Item(OutboxStatus.Sent, TimeSpan.Zero);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.OutboxItems.AddRange(waiting, later, sent);
			await context.SaveChangesAsync();
		});

		var tasks = await harness.UsingAsync(services => services.GetRequiredService<MailHub>().GetBackgroundTasks());

		Assert.Contains(tasks, task => task.Label == "Sending 1 message" && task.Running);
		Assert.Contains(tasks, task => task.Label == "1 message scheduled to send later" && !task.Running);
		Assert.Equal(2, tasks.Count);
	}

	[Fact]
	public async Task Running_work_shows_with_its_progress_until_it_ends()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			var tracker = services.GetRequiredService<BackgroundActivityTracker>();

			var task = tracker.Begin("Rebuilding message previews", harness.Account.Id);
			task.Report(40, 100);
			var shown = Assert.Single(await hub.GetBackgroundTasks());
			Assert.Equal("Rebuilding message previews", shown.Label);
			Assert.Equal(harness.Account.DisplayName, shown.AccountName);
			Assert.Equal((40, 100), (shown.Done, shown.Total));

			task.Dispose();
			Assert.Empty(await hub.GetBackgroundTasks());
		});
	}

	[Fact]
	public async Task A_change_awaiting_a_check_with_the_provider_is_reported_as_such_not_as_waiting_to_send()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var messageId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(new Message { Id = messageId, AccountId = harness.Account.Id, IsRead = false });
			await context.SaveChangesAsync();
			await services.GetRequiredService<MailHub>().SetFlags(harness.Account.Id, [messageId], true, null);
			var item = await context.MutationItems.SingleAsync();
			item.State = MutationState.Leased;
			context.MutationExecutionAttempts.Add(
				new MutationExecutionAttempt
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					Provider = harness.Account.ProviderType,
					OperationKind = MutationOperationKind.SetFlags,
					State = MutationAttemptState.Ambiguous,
					CreatedAt = DateTimeOffset.UnixEpoch,
					Items = [new MutationExecutionAttemptItem { MutationItemId = item.Id }],
				}
			);
			await context.SaveChangesAsync();
		});

		var tasks = await harness.UsingAsync(services => services.GetRequiredService<MailHub>().GetBackgroundTasks());

		var shown = Assert.Single(tasks);
		Assert.StartsWith("Checking with ", shown.Label);
		Assert.True(shown.Running);
	}
}
