using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class CalendarSyncCrashWindowTests
{
	[Fact]
	public async Task A_precommit_crash_replays_the_calendar_page_before_advancing_its_token()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Faults.ArmAt(FaultPoints.SyncPageBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SynchronizeAsync(harness));
		await harness.RestartAsync();

		await SynchronizeAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal("token-1", (await context.Calendars.SingleAsync()).SyncCursor);
			Assert.Single(await context.CalendarEvents.ToListAsync());
		});
		Assert.Equal([null, null], harness.CalendarProvider.Cursors);
	}

	[Fact]
	public async Task A_postcommit_crash_resumes_from_the_committed_calendar_token()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.Faults.ArmAt(FaultPoints.SyncPageAfterCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SynchronizeAsync(harness));
		await harness.RestartAsync();

		await SynchronizeAsync(harness);
		Assert.Equal([null, "token-1"], harness.CalendarProvider.Cursors);
	}

	[Fact]
	public async Task An_invalid_baseline_continuation_discards_its_partial_page_before_restart()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		harness.CalendarProvider.InvalidateFirstBaselineContinuation = true;

		await SynchronizeAsync(harness);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal("token-1", (await context.Calendars.SingleAsync()).SyncCursor);
			var ev = await context.CalendarEvents.SingleAsync();
			Assert.Equal("one", ev.ProviderEventId);
		});
		Assert.Equal([null, null, null], harness.CalendarProvider.Cursors);
	}

	private static Task SynchronizeAsync(SyncHarness harness) => harness.UsingAsync(async scope =>
	{
		var account = await harness.AccountInScopeAsync(scope);
		await scope.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
	});
}
