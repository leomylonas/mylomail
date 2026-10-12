using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>Manual "send &amp; receive": one pass now, without owning or doubling any poll loop.</summary>
public sealed class SyncNowTests
{
	private static async Task<SyncHarness> CoveredGmailAsync()
	{
		var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		harness.Provider.SeedMessage("INBOX", Guid.NewGuid(), DateTimeOffset.UnixEpoch);
		await SyncTests.ReconcileAsync(harness);
		await SyncTests.SyncAsync(harness);
		await SyncTests.CoverAsync(harness);
		return harness;
	}

	private static Task<SyncNowOutcome> SyncNowAsync(SyncHarness harness) =>
		harness.UsingAsync(scope => scope.GetRequiredService<SyncJobs>().SyncNowAsync(harness.Account.Id));

	[Fact]
	public async Task Mail_that_arrived_since_the_last_poll_is_applied_at_once()
	{
		await using var harness = await CoveredGmailAsync();
		var arrived = Guid.NewGuid();
		harness.Provider.SeedMessage("INBOX", arrived, DateTimeOffset.UnixEpoch.AddDays(1));

		Assert.Equal(SyncNowOutcome.Synced, await SyncNowAsync(harness));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.Messages.AnyAsync(m => m.ProviderStableId == $"message-{arrived}"));
		});
	}

	[Fact]
	public async Task A_throttled_account_is_not_asked_anything()
	{
		await using var harness = await CoveredGmailAsync();
		await harness.UsingAsync(scope =>
		{
			scope.GetRequiredService<AccountGate>().Throttle(harness.Account.Id, TimeSpan.FromMinutes(1));
			return Task.CompletedTask;
		});
		var callsBefore = harness.Provider.TopologyCursors.Count;

		Assert.Equal(SyncNowOutcome.Throttled, await SyncNowAsync(harness));
		Assert.Equal(callsBefore, harness.Provider.TopologyCursors.Count);
	}

	[Fact]
	public async Task An_account_waiting_for_the_user_to_sign_in_again_is_reported_not_retried()
	{
		await using var harness = await CoveredGmailAsync();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			(await context.Accounts.SingleAsync()).AuthState = AuthState.NeedsReauth;
			await context.SaveChangesAsync();
		});

		Assert.Equal(SyncNowOutcome.NeedsAttention, await SyncNowAsync(harness));
	}
}
