using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

public sealed class AccountQueueTests
{
	public sealed class Probe
	{
		public static readonly SemaphoreSlim Release = new(0);
		public static readonly TaskCompletionSource<Guid> Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public static readonly CountdownEvent Blocked = new(4);

		public static void Block(Guid accountId)
		{
			Blocked.Signal();
			Release.Wait(TimeSpan.FromSeconds(30));
		}

		public static void Quick(Guid accountId) => Finished.TrySetResult(accountId);
	}

	/// <summary>
	/// An account whose every worker is occupied (a long backlog, a slow or rate-limited provider)
	/// must not delay another account's jobs.
	/// </summary>
	[Fact]
	public async Task A_saturated_account_does_not_delay_another_account()
	{
		var services = new ServiceCollection();
		services.AddSingleton(new InMemoryStorage());
		services.AddSingleton<JobStorage>(p => p.GetRequiredService<InMemoryStorage>());
		services.AddSingleton<AccountQueueServers>(p => new AccountQueueServers(p.GetRequiredService<JobStorage>()));
		await using var provider = services.BuildServiceProvider();
		var storage = provider.GetRequiredService<JobStorage>();

		var filters = new JobFilterCollection { new AccountQueueFilter(provider) };
		var client = new BackgroundJobClient(storage, filters);

		var saturated = Guid.NewGuid();
		var other = Guid.NewGuid();

		for (var i = 0; i < 4; i++)
		{
			client.Enqueue(() => Probe.Block(saturated));
		}

		Assert.True(Probe.Blocked.Wait(TimeSpan.FromSeconds(20)), "the saturating jobs should all be running");

		client.Enqueue(() => Probe.Quick(other));
		var finished = await Task.WhenAny(Probe.Finished.Task, Task.Delay(TimeSpan.FromSeconds(20)));

		Probe.Release.Release(4);
		Assert.Same(Probe.Finished.Task, finished);
		Assert.Equal(other, await Probe.Finished.Task);
	}
}
