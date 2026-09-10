using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
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

	/// <summary>
	/// A remote event can exist before the synchronous create response reaches the local
	/// database. The UID and dispatch marker must recover that one remote event, not issue
	/// another create merely because the original process died (§6).
	/// </summary>
	[Fact]
	public async Task An_initial_calendar_creation_crash_recovers_the_remote_event_without_creating_a_duplicate()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		var calendarId = await SeedCalendarAsync(harness);
		harness.Faults.ArmAt(FaultPoints.CalendarCreateAfterProviderCallBeforeCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(() =>
			harness.UsingAsync(async scope =>
				await scope.GetRequiredService<CalendarEventService>().SaveAsync(
					new CalendarEventInput(
						null,
						calendarId,
						"Standup",
						null,
						null,
						DateTimeOffset.UnixEpoch,
						DateTimeOffset.UnixEpoch.AddHours(1),
						false
					)
				)
			)
		);
		Assert.Equal(1, harness.CalendarProvider.CreateCalls);

		await harness.RestartAsync();
		await SynchronizeAsync(harness);

		Assert.Equal(1, harness.CalendarProvider.CreateCalls);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var eventRow = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "created-1");
			Assert.Equal("created-revision", eventRow.ProviderRevision);
			Assert.Empty(await context.CalendarCreationAttempts.ToListAsync());
		});
	}

	private static Task<Guid> SeedCalendarAsync(SyncHarness harness) => harness.UsingAsync(async scope =>
	{
		var calendar = new Calendar
		{
			Id = Guid.NewGuid(),
			AccountId = harness.Account.Id,
			ProviderCalendarId = "calendar",
			Name = "Calendar",
		};
		var context = scope.GetRequiredService<MyloMailDbContext>();
		context.Calendars.Add(calendar);
		await context.SaveChangesAsync();
		return calendar.Id;
	});

	private static Task SynchronizeAsync(SyncHarness harness) => harness.UsingAsync(async scope =>
	{
		var account = await harness.AccountInScopeAsync(scope);
		await scope.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
	});
}
