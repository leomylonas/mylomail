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
/// CalendarSyncService.ApplyPageAsync's recurrence-master resolution was rewritten (fifty-fourth
/// pass) from one SingleAsync/SingleOrDefaultAsync pair per upserted event into two batched
/// queries. The only prior recurrence test (<see cref="CalendarRecurrenceDeletionTests"/>)
/// upserts a master and its override in the same provider page, which doesn't exercise the case
/// the batching had to get right on purpose: a master's local id living in an *earlier* page,
/// looked up from a *later* page that only knows its provider id, and an override arriving
/// *before* its master exists at all.
/// </summary>
public sealed class CalendarRecurrenceCrossPageLinkingTests
{
	[Fact]
	public async Task An_override_upserted_after_its_master_synced_in_an_earlier_page_still_links()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var provider = new TwoPageCalendarProvider();
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
			var master = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "master");
			Assert.Null(master.RecurrenceMasterId);

			var overrideInstance = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "master#override-1");
			Assert.Equal(master.Id, overrideInstance.RecurrenceMasterId);
		}
	}

	[Fact]
	public async Task An_absolute_legacy_CalDAV_resource_identity_is_normalized_and_updated_in_place()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var provider = new TwoPageCalendarProvider { MasterId = "/collection/item.ics" };
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
		var calendarId = Guid.NewGuid();
		var legacyEventId = Guid.NewGuid();
		var newerAliasEventId = Guid.NewGuid();
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(new Account { Id = accountId, ProviderType = ProviderType.Imap });
			context.Calendars.Add(new Calendar { Id = calendarId, AccountId = accountId, ProviderCalendarId = "calendar", Name = "Calendar" });
			context.CalendarEvents.Add(
				new CalendarEvent
				{
					Id = legacyEventId,
					CalendarId = calendarId,
					ProviderEventId = "https://calendar.example.test/collection/item.ics",
					ICalUid = "series",
					ProviderRevision = "old",
					SyncConflict = true,
				}
			);
			context.CalendarEvents.Add(
				new CalendarEvent
				{
					Id = newerAliasEventId,
					CalendarId = calendarId,
					ProviderEventId = "/collection/item.ics",
					ICalUid = "series",
					ProviderRevision = "newer",
				}
			);
			await context.SaveChangesAsync();
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			await scope.ServiceProvider.GetRequiredService<CalendarSyncService>()
				.SynchronizeAsync(await context.Accounts.SingleAsync(row => row.Id == accountId));
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var master = await context.CalendarEvents.SingleAsync(row => row.Id == legacyEventId);
			Assert.Null(await context.CalendarEvents.FindAsync(newerAliasEventId));
			Assert.Equal("/collection/item.ics", master.ProviderEventId);
			Assert.Equal(2, await context.CalendarEvents.CountAsync());
			Assert.Equal(
				legacyEventId,
				(await context.CalendarEvents.SingleAsync(row => row.ProviderEventId == "/collection/item.ics#override-1")).RecurrenceMasterId
			);
		}
	}

	/// <summary>
	/// The mirror case: the override arrives on page 1, before its master exists anywhere
	/// locally, so it must be resolved as a "waiting child" once the master lands on page 2 —
	/// the second-loop path, not the first.
	/// </summary>
	[Fact]
	public async Task An_override_upserted_before_its_master_is_linked_once_the_master_arrives()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var provider = new TwoPageCalendarProvider { OverrideFirst = true };
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
			var master = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "master");
			var overrideInstance = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "master#override-1");
			Assert.Equal(master.Id, overrideInstance.RecurrenceMasterId);
		}
	}

	/// <summary>
	/// An occurrence skipped this sync because it's flagged <c>SyncConflict</c> never runs
	/// through <c>Apply</c>, so its stored <c>RecurrenceMasterProviderEventId</c> keeps its old
	/// value rather than picking up whatever the incoming dto now reports. The batched
	/// resolution must key off that stored value, not the dto's, or a conflicted occurrence
	/// would get silently re-parented to a master it was never actually re-linked to.
	/// </summary>
	[Fact]
	public async Task A_conflicted_occurrence_keeps_its_old_master_link_not_the_incoming_ones()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var provider = new RelinkingCalendarProvider();
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

		Guid oldMasterId;
		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			oldMasterId = (await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "old-master")).Id;
			var child = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "child");
			Assert.Equal(oldMasterId, child.RecurrenceMasterId);

			// Flip the flag directly, the way a rejected local edit would (§15) — the provider
			// round trip that normally sets this isn't the thing under test here.
			child.SyncConflict = true;
			await context.SaveChangesAsync();
		}

		provider.RelinkNext = true;
		await using (var scope = services.CreateAsyncScope())
		{
			var account = await scope.ServiceProvider.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == accountId);
			await scope.ServiceProvider.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
		}

		await using (var scope = services.CreateAsyncScope())
		{
			var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
			var child = await context.CalendarEvents.SingleAsync(e => e.ProviderEventId == "child");
			// Still conflicted, still pointing at the old master — the incoming "new-master"
			// relink was never applied because Apply() never ran for a conflicted row.
			Assert.True(child.SyncConflict);
			Assert.Equal(oldMasterId, child.RecurrenceMasterId);
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

	/// <summary>Splits the master and its override across two pages of one sync pass.</summary>
	private sealed class TwoPageCalendarProvider : ICalendarProvider
	{
		/// <summary>True puts the override on page 1 and the master on page 2 — the "waiting
		/// children" path. False (default) puts the master first and the override second —
		/// the ordinary cross-page master-lookup path.</summary>
		public bool OverrideFirst { get; set; }

		public string MasterId { get; set; } = "master";

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
			var master = new CalendarEventDto
			{
				ProviderEventId = MasterId,
				ICalUid = "series",
				ProviderRevision = "etag-1",
				Sequence = 3,
				Title = "Standup",
				Start = DateTimeOffset.UnixEpoch,
				End = DateTimeOffset.UnixEpoch.AddHours(1),
				RecurrenceRules = ["FREQ=DAILY"],
			};
			var overrideInstance = new CalendarEventDto
			{
				ProviderEventId = $"{MasterId}#override-1",
				ICalUid = "series",
				ProviderRevision = "etag-1",
				Title = "Standup (moved)",
				Start = DateTimeOffset.UnixEpoch.AddDays(1),
				End = DateTimeOffset.UnixEpoch.AddDays(1).AddHours(1),
				RecurrenceMasterProviderEventId = MasterId,
			};

			var firstItem = OverrideFirst ? overrideInstance : master;
			var secondItem = OverrideFirst ? master : overrideInstance;

			if (continuation is null)
			{
				return Task.FromResult(new CalendarSyncResult("token-1", "page-2", [firstItem], []));
			}
			return Task.FromResult(new CalendarSyncResult("token-2", null, [secondItem], []));
		}

		public Task<CalendarEventCreation> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<CalendarEventDto?> FindEventAsync(
			Account account,
			Calendar calendar,
			string stableICalUid,
			string providerCreationKey,
			CancellationToken ct
		) => Task.FromResult<CalendarEventDto?>(null);

		public Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task RespondToInviteAsync(
			Account account,
			CalendarEvent ev,
			InviteResponse response,
			string? comment,
			Address replyingAs,
			CancellationToken ct
		) => throw new NotSupportedException();
	}

	/// <summary>First sync creates "old-master" and a "child" linked to it; the second sync
	/// (once <see cref="RelinkNext"/> is set) reports "child" again with a different
	/// RecurrenceMasterProviderEventId, simulating what the provider would send if the series
	/// were ever actually re-parented.</summary>
	private sealed class RelinkingCalendarProvider : ICalendarProvider
	{
		public bool RelinkNext { get; set; }

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
			if (!RelinkNext)
			{
				var oldMaster = new CalendarEventDto
				{
					ProviderEventId = "old-master",
					ICalUid = "old-series",
					ProviderRevision = "etag-1",
					Title = "Standup",
					Start = DateTimeOffset.UnixEpoch,
					End = DateTimeOffset.UnixEpoch.AddHours(1),
				};
				var child = new CalendarEventDto
				{
					ProviderEventId = "child",
					ICalUid = "old-series",
					ProviderRevision = "etag-1",
					Title = "Standup (moved)",
					Start = DateTimeOffset.UnixEpoch.AddDays(1),
					End = DateTimeOffset.UnixEpoch.AddDays(1).AddHours(1),
					RecurrenceMasterProviderEventId = "old-master",
				};
				return Task.FromResult(new CalendarSyncResult("token-1", null, [oldMaster, child], []));
			}

			var relinkedChild = new CalendarEventDto
			{
				ProviderEventId = "child",
				ICalUid = "new-series",
				ProviderRevision = "etag-2",
				Title = "Standup (moved again)",
				Start = DateTimeOffset.UnixEpoch.AddDays(1),
				End = DateTimeOffset.UnixEpoch.AddDays(1).AddHours(1),
				RecurrenceMasterProviderEventId = "new-master",
			};
			return Task.FromResult(new CalendarSyncResult("token-2", null, [relinkedChild], []));
		}

		public Task<CalendarEventCreation> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task<CalendarEventDto?> FindEventAsync(
			Account account,
			Calendar calendar,
			string stableICalUid,
			string providerCreationKey,
			CancellationToken ct
		) => Task.FromResult<CalendarEventDto?>(null);

		public Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct) =>
			throw new NotSupportedException();

		public Task RespondToInviteAsync(
			Account account,
			CalendarEvent ev,
			InviteResponse response,
			string? comment,
			Address replyingAs,
			CancellationToken ct
		) => throw new NotSupportedException();
	}
}
