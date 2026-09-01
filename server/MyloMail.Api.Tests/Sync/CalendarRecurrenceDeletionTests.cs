using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// A provider reports one resource deleted, not one event: CalDAV's master and its
/// overrides share an href. Deleting the master must take its overrides with it rather
/// than leaving them as orphaned standalone events (§1 — recurrence is a set).
/// </summary>
public sealed class CalendarRecurrenceDeletionTests
{
	[Fact]
	public async Task Deleting_a_recurring_master_removes_its_override_instances_too()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var provider = new ScriptedCalendarProvider();
		var services = new ServiceCollection()
			.AddLogging()
			.AddPersistence(database.Directory)
			.AddSingleton<IMailProviderFactory>(new StubMailFactory())
			.AddSingleton<ICalendarProviderFactory>(new StubCalendarFactory(provider))
			.AddMutations()
			.AddSync()
			.BuildServiceProvider();
		await using var _ = services;

		var accountId = Guid.NewGuid();
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(
				new Account
				{
					Id = accountId,
					ProviderType = ProviderType.Imap,
					InitialSyncMode = InitialSyncMode.Full,
				}
			);
			await context.SaveChangesAsync();
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var account = await scope.ServiceProvider.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == accountId);
			await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var calendar = await context.Calendars.SingleAsync();
			Assert.Equal("Calendar", calendar.Name);
			Assert.True(calendar.IsDefault);
			Assert.Equal("token-1", calendar.SyncCursor);

			var master = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "master");
			Assert.Equal("series", master.ICalUid);
			Assert.Equal(3, master.Sequence);
			Assert.Equal("Standup", master.Title);
			Assert.Equal("Room 4", master.Location);
			Assert.Equal(DateTimeOffset.UnixEpoch, master.Start);
			Assert.Equal(DateTimeOffset.UnixEpoch.AddHours(1), master.End);
			Assert.Equal(["FREQ=DAILY"], master.RecurrenceRules);
			Assert.Null(master.RecurrenceMasterId);

			var overrideInstance = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "master#override-1");
			Assert.Equal("Standup (moved)", overrideInstance.Title);
			Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), overrideInstance.Start);
			Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), overrideInstance.RecurrenceId);
			Assert.Equal(master.Id, overrideInstance.RecurrenceMasterId);
		}

		provider.DeleteMasterNext = true;
		await using (var scope = services.CreateAsyncScope())
		{
			var account = await scope.ServiceProvider.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == accountId);
			await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			Assert.Empty(await context.CalendarEvents.ToListAsync());
		}
	}

	private sealed class StubMailFactory : IMailProviderFactory
	{
		public IMailProvider For(Account account) => throw new NotSupportedException();
	}

	private sealed class StubCalendarFactory(ICalendarProvider provider) : ICalendarProviderFactory
	{
		public ICalendarProvider For(Account account) => provider;
	}

	private sealed class ScriptedCalendarProvider : ICalendarProvider
	{
		public bool DeleteMasterNext { get; set; }

		public ProviderType Type => ProviderType.Imap;

		public Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct) =>
			Task.FromResult<IReadOnlyList<CalendarDto>>([new("calendar", "Calendar", null, true)]);

		public Task<CalendarSyncResult> SyncCalendarAsync(
			Account account,
			Calendar calendar,
			string? cursor,
			string? continuation,
			CancellationToken ct
		)
		{
			if (DeleteMasterNext)
			{
				return Task.FromResult(new CalendarSyncResult("token-2", null, [], ["master"]));
			}

			var master = new CalendarEventDto
			{
				ProviderEventId = "master",
				ICalUid = "series",
				ProviderRevision = "etag-1",
				Sequence = 3,
				Title = "Standup",
				Location = "Room 4",
				Start = DateTimeOffset.UnixEpoch,
				End = DateTimeOffset.UnixEpoch.AddHours(1),
				RecurrenceRules = ["FREQ=DAILY"],
			};
			var overrideInstance = new CalendarEventDto
			{
				ProviderEventId = "master#override-1",
				ICalUid = "series",
				ProviderRevision = "etag-1",
				Title = "Standup (moved)",
				Start = DateTimeOffset.UnixEpoch.AddDays(1),
				End = DateTimeOffset.UnixEpoch.AddDays(1).AddHours(1),
				RecurrenceMasterProviderEventId = "master",
				RecurrenceId = DateTimeOffset.UnixEpoch.AddDays(1),
			};
			return Task.FromResult(new CalendarSyncResult("token-1", null, [master, overrideInstance], []));
		}

		public Task<string> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task RespondToInviteAsync(
			Account account,
			CalendarEvent ev,
			InviteResponse response,
			string? comment,
			CancellationToken ct
		) => throw new NotSupportedException();
	}
}
