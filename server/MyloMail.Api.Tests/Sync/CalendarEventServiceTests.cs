using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

public sealed class CalendarEventServiceTests
{
	[Fact]
	public async Task Creating_an_event_asks_the_provider_first_and_stores_its_identity()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);

		var saved = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		Assert.Equal("created-1", saved.ProviderEventId);
		Assert.False(string.IsNullOrEmpty(saved.ICalUid));
		Assert.Equal(1, provider.CreateCalls);
		await harness.UsingAsync(async scope =>
		{
			var row = await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.SingleAsync();
			Assert.Equal("Standup", row.Title);
		});
		Assert.Contains(saved.Id, harness.Events.CalendarEvents);
	}

	[Fact]
	public async Task Updating_an_event_keeps_the_local_edit_and_flags_a_conflict_on_a_precondition_failure()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var created = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		provider.RejectNextUpdate = true;
		var updated = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(created.Id, harness.CalendarId, "Standup (moved)", "Room 2", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		// The edit is kept, not discarded or overwritten, even though the push failed (§15).
		Assert.Equal("Standup (moved)", updated.Title);
		Assert.Equal("Room 2", updated.Location);
		Assert.True(updated.SyncConflict);
		Assert.Contains(updated.Id, harness.Events.CalendarConflicts);
	}

	[Fact]
	public async Task Deleting_a_recurring_master_removes_its_overrides_too()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var master = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var m = new CalendarEvent { Id = Guid.NewGuid(), CalendarId = harness.CalendarId, ProviderEventId = "master", ICalUid = "series" };
			var child = new CalendarEvent
			{
				Id = Guid.NewGuid(),
				CalendarId = harness.CalendarId,
				ProviderEventId = "master#override",
				ICalUid = "series",
				RecurrenceMasterId = m.Id,
			};
			context.CalendarEvents.AddRange(m, child);
			await context.SaveChangesAsync();
			return m;
		});

		await harness.UsingAsync(scope => scope.GetRequiredService<CalendarEventService>().DeleteAsync(master.Id));

		await harness.UsingAsync(async scope =>
		{
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.ToListAsync());
		});
		Assert.Equal(1, provider.DeleteCalls);
	}

	[Fact]
	public async Task Deleting_an_event_the_provider_rejects_leaves_it_in_place()
	{
		var provider = new ScriptedCalendarProvider { RejectNextDelete = true };
		await using var harness = await Harness.CreateAsync(provider);
		var created = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		await Assert.ThrowsAsync<ProviderConflictException>(
			() => harness.UsingAsync(scope => scope.GetRequiredService<CalendarEventService>().DeleteAsync(created.Id))
		);

		await harness.UsingAsync(async scope =>
		{
			Assert.Equal(1, await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.CountAsync());
		});
	}

	private sealed class Harness : IAsyncDisposable
	{
		private readonly TestDatabase database;
		private readonly ServiceProvider services;

		private Harness(TestDatabase database, ServiceProvider services, Guid calendarId, RecordingHubEvents events)
		{
			this.database = database;
			this.services = services;
			CalendarId = calendarId;
			Events = events;
		}

		public Guid CalendarId { get; }

		public RecordingHubEvents Events { get; }

		public static async Task<Harness> CreateAsync(ICalendarProvider provider)
		{
			var database = new TestDatabase();
			await database.MigrateAsync();
			var events = new RecordingHubEvents();
			var services = new ServiceCollection()
				.AddLogging()
				.AddPersistence(database.Directory)
				.AddSingleton<IMailProviderFactory>(new StubMailFactory())
				.AddSingleton<ICalendarProviderFactory>(new StubCalendarFactory(provider))
				.AddSingleton<IHubEvents>(events)
				.AddMutations()
				.AddSync()
				.BuildServiceProvider();

			Guid calendarId;
			await using (var scope = services.CreateAsyncScope())
			{
				var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
				var account = new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap, InitialSyncMode = InitialSyncMode.Full };
				var calendar = new Calendar { Id = Guid.NewGuid(), AccountId = account.Id, ProviderCalendarId = "cal", Name = "Calendar" };
				context.Accounts.Add(account);
				context.Calendars.Add(calendar);
				await context.SaveChangesAsync();
				calendarId = calendar.Id;
			}

			return new Harness(database, services, calendarId, events);
		}

		public async Task<T> UsingAsync<T>(Func<IServiceProvider, Task<T>> work)
		{
			await using var scope = services.CreateAsyncScope();
			return await work(scope.ServiceProvider);
		}

		public Task UsingAsync(Func<IServiceProvider, Task> work) =>
			UsingAsync(async scope =>
			{
				await work(scope);
				return true;
			});

		public async ValueTask DisposeAsync()
		{
			await services.DisposeAsync();
			await database.DisposeAsync();
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
		public int CreateCalls { get; private set; }
		public int DeleteCalls { get; private set; }
		public bool RejectNextUpdate { get; set; }
		public bool RejectNextDelete { get; set; }

		public ProviderType Type => ProviderType.Imap;

		public Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<CalendarSyncResult> SyncCalendarAsync(Account account, Calendar calendar, string? cursor, string? continuation, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<string> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct)
		{
			CreateCalls++;
			return Task.FromResult($"created-{CreateCalls}");
		}

		public Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct)
		{
			if (RejectNextUpdate)
			{
				RejectNextUpdate = false;
				throw new ProviderConflictException("stale");
			}
			return Task.CompletedTask;
		}

		public Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct)
		{
			if (RejectNextDelete)
			{
				RejectNextDelete = false;
				throw new ProviderConflictException("stale");
			}
			DeleteCalls++;
			return Task.CompletedTask;
		}

		public Task RespondToInviteAsync(Account account, CalendarEvent ev, InviteResponse response, string? comment, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
