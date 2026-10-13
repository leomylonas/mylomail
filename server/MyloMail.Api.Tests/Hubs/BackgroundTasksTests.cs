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
				(await services.GetRequiredService<MailHub>().GetBackgroundTasks()).Count(task => task.Label.StartsWith("Sending")));

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
}
