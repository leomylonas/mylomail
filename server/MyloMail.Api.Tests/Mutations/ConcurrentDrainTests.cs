using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

public sealed class ConcurrentDrainTests
{
	/// <summary>
	/// A user action asks for a drain, so a quick run of actions starts several. A drain settles the
	/// dispatched attempts it can see before executing, and one still being executed by the drain beside
	/// it is not interrupted work to recover: asked of the provider before it has answered, it looked
	/// undone and was put back to be done again.
	/// </summary>
	[Fact]
	public async Task A_second_drain_waits_for_the_first_instead_of_settling_the_attempt_it_is_still_executing()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		var providerCallOpen = new TaskCompletionSource();
		var releaseProvider = new TaskCompletionSource();
		harness.Provider.DuringSetFlagsAsync = async () =>
		{
			providerCallOpen.TrySetResult();
			await releaseProvider.Task;
		};

		var first = harness.UsingAsync(scope => scope.GetRequiredService<MutationJobs>().DrainAsync(harness.AccountId));
		await providerCallOpen.Task;
		var second = harness.UsingAsync(scope => scope.GetRequiredService<MutationJobs>().DrainAsync(harness.AccountId));

		// Long enough for a drain that was not held back to have settled and re-run the item.
		await Task.Delay(500);
		Assert.False(second.IsCompleted);

		releaseProvider.SetResult();
		await Task.WhenAll(first, second);

		Assert.Equal(1, harness.Provider.SetFlagsCalls);
		var state = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().MutationItems.Where(i => i.Id == item.Id).Select(i => i.State).SingleAsync()
		);
		Assert.Equal(MutationState.Completed, state);
	}
}
