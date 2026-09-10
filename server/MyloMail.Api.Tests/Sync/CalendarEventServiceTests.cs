using Microsoft.AspNetCore.SignalR;
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
	public async Task Resolving_a_conflict_by_keeping_mine_force_overwrites_and_clears_the_flag()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var created = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);
		provider.RejectNextUpdate = true;
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(created.Id, harness.CalendarId, "Standup (moved)", "Room 2", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		var resolved = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().ResolveConflictAsync(created.Id, keepMine: true)
		);

		Assert.False(resolved.SyncConflict);
		Assert.Equal("Standup (moved)", resolved.Title);
		// No precondition on the forced overwrite — the user chose this explicitly.
		Assert.Null(provider.LastUpdateETag);
	}

	[Fact]
	public async Task Resolving_a_conflict_by_keeping_theirs_pulls_the_servers_version_and_clears_the_flag()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var created = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);
		provider.RejectNextUpdate = true;
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(created.Id, harness.CalendarId, "Standup (moved)", "Room 2", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		// What the "server" now holds, as scripted for the sync this triggers.
		provider.ScriptSync(
			new CalendarSyncResult(
				"cursor-1",
				null,
				[
					new CalendarEventDto
					{
						ProviderEventId = created.ProviderEventId!,
						ICalUid = created.ICalUid,
						ProviderRevision = "etag-2",
						Title = "Standup (server title)",
						Start = DateTimeOffset.UnixEpoch,
						End = DateTimeOffset.UnixEpoch.AddHours(1),
					},
				],
				[]
			)
		);

		var resolved = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().ResolveConflictAsync(created.Id, keepMine: false)
		);

		Assert.False(resolved.SyncConflict);
		Assert.Equal("Standup (server title)", resolved.Title);
		Assert.Null(resolved.Location);
	}

	[Fact]
	public async Task A_routine_sync_pass_leaves_a_still_unresolved_conflict_untouched()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var created = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);
		provider.RejectNextUpdate = true;
		await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(created.Id, harness.CalendarId, "Standup (moved)", "Room 2", null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		// Exactly what would arrive on an ordinary background poll, reporting the same
		// server-side change that caused the conflict in the first place.
		provider.ScriptSync(
			new CalendarSyncResult(
				"cursor-1",
				null,
				[
					new CalendarEventDto
					{
						ProviderEventId = created.ProviderEventId!,
						ICalUid = created.ICalUid,
						ProviderRevision = "etag-2",
						Title = "Standup (server title)",
						Start = DateTimeOffset.UnixEpoch,
						End = DateTimeOffset.UnixEpoch.AddHours(1),
					},
				],
				[]
			)
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			// No resolvingEventId: this is routine polling, not a user-requested resolution.
			await scope.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
		});

		await harness.UsingAsync(async scope =>
		{
			var row = await scope
				.GetRequiredService<MyloMailDbContext>()
				.CalendarEvents.SingleAsync(e => e.Id == created.Id);
			// The local edit and the flag both survive — routine sync must never silently
			// pick a side (§15).
			Assert.True(row.SyncConflict);
			Assert.Equal("Standup (moved)", row.Title);
			Assert.Equal("Room 2", row.Location);
		});
	}

	/// <summary>
	/// A mailed invite (§13 Epic 7) may materialise an event under an account's local-only
	/// pseudo-calendar before that account has ever had a real calendar synced. Once a real
	/// sync does happen and reports the same invite by UID, it must adopt the existing row —
	/// not create a duplicate the mail-materialised one now sits orphaned next to.
	/// </summary>
	[Fact]
	public async Task A_routine_sync_adopts_a_mail_materialised_event_sharing_its_uid()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var materialisedId = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			var localCalendar = new Calendar
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderCalendarId = "local-invites",
				Name = "Invites",
				IsLocalOnly = true,
			};
			context.Calendars.Add(localCalendar);
			var ev = new CalendarEvent
			{
				Id = Guid.NewGuid(),
				CalendarId = localCalendar.Id,
				ProviderEventId = "mail:1",
				ICalUid = "shared-uid",
				Title = "Standup",
				Start = DateTimeOffset.UnixEpoch,
				End = DateTimeOffset.UnixEpoch.AddHours(1),
			};
			context.CalendarEvents.Add(ev);
			await context.SaveChangesAsync();
			return ev.Id;
		});

		provider.ScriptSync(
			new CalendarSyncResult(
				"cursor-1",
				null,
				[
					new CalendarEventDto
					{
						ProviderEventId = "real-1",
						ICalUid = "shared-uid",
						ProviderRevision = "etag-1",
						Title = "Standup",
						Start = DateTimeOffset.UnixEpoch,
						End = DateTimeOffset.UnixEpoch.AddHours(1),
					},
				],
				[]
			)
		);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync();
			await scope.GetRequiredService<CalendarSyncService>().SynchronizeAsync(account);
			return true;
		});

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(1, await context.CalendarEvents.CountAsync(e => e.ICalUid == "shared-uid"));
			var adopted = await context.CalendarEvents.SingleAsync(e => e.ICalUid == "shared-uid");
			Assert.Equal(materialisedId, adopted.Id);
			Assert.Equal(harness.CalendarId, adopted.CalendarId);
			Assert.Equal("real-1", adopted.ProviderEventId);
			return true;
		});
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

	/// <summary>
	/// Two-hundred-and-nineteenth pass: deleting one occurrence of a recurring series must not
	/// let the deleted slot reappear as a "ghost" occurrence before the next sync. The CalDAV
	/// provider's own <c>DeleteEventAsync</c> marks a recurrence-override instance
	/// <c>STATUS:CANCELLED</c> in place rather than deleting the resource (§1) — the local row
	/// must reflect that same outcome, not be removed outright, since
	/// <see cref="CalendarEventOccurrences.ForCalendarAsync"/> only suppresses a generated
	/// occurrence at a slot that still has an override row keyed by that <c>RecurrenceId</c>.
	/// </summary>
	[Fact]
	public async Task Deleting_a_recurrence_override_cancels_it_in_place_instead_of_resurrecting_a_ghost_occurrence()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var firstOccurrence = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
		var secondOccurrence = firstOccurrence.AddDays(7);

		var overrideId = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var master = new CalendarEvent
			{
				Id = Guid.NewGuid(),
				CalendarId = harness.CalendarId,
				Title = "Standup",
				ProviderEventId = "master-1",
				Start = firstOccurrence,
				End = firstOccurrence.AddMinutes(30),
				RecurrenceRules = ["FREQ=WEEKLY;COUNT=6"],
				Status = EventStatus.Confirmed,
			};
			var overrideEvent = new CalendarEvent
			{
				Id = Guid.NewGuid(),
				CalendarId = harness.CalendarId,
				Title = "Standup (moved to the afternoon)",
				ProviderEventId = "master-1#override",
				Start = secondOccurrence.AddHours(6),
				End = secondOccurrence.AddHours(6.5),
				RecurrenceMasterId = master.Id,
				RecurrenceId = secondOccurrence,
				Status = EventStatus.Confirmed,
			};
			context.CalendarEvents.AddRange(master, overrideEvent);
			await context.SaveChangesAsync();
			return overrideEvent.Id;
		});

		await harness.UsingAsync(scope => scope.GetRequiredService<CalendarEventService>().DeleteAsync(overrideId));

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();

			// The row survives, cancelled in place — not removed.
			var row = await context.CalendarEvents.SingleAsync(e => e.Id == overrideId);
			Assert.Equal(EventStatus.Cancelled, row.Status);

			// And no virtual "ghost" occurrence is generated at the slot it used to occupy.
			var window = await CalendarEventOccurrences.ForCalendarAsync(
				context,
				harness.CalendarId,
				secondOccurrence.AddDays(-1),
				secondOccurrence.AddDays(1)
			);
			Assert.DoesNotContain(window, e => e.IsVirtualOccurrence);
			var atThatSlot = Assert.Single(window);
			Assert.Equal(overrideId, atThatSlot.Id);
			Assert.Equal(EventStatus.Cancelled, atThatSlot.Status);
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

		// The raw ProviderConflictException is rethrown as a HubException with the same message
		// (sixty-sixth pass): SignalR's default EnableDetailedErrors=false replaces anything
		// that isn't a HubException with a generic "unexpected error", which would otherwise
		// silently defeat EventModal.tsx's error banner.
		var thrown = await Assert.ThrowsAsync<HubException>(
			() => harness.UsingAsync(scope => scope.GetRequiredService<CalendarEventService>().DeleteAsync(created.Id))
		);
		Assert.Equal("stale", thrown.Message);

		await harness.UsingAsync(async scope =>
		{
			Assert.Equal(1, await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.CountAsync());
		});
	}

	/// <summary>
	/// Sixty-sixth pass: same bug shape as the sixty-fifth pass's mailbox fix, here for
	/// calendar events — a provider rejection on create/update/delete/RSVP-reply was left to
	/// propagate raw, so SignalR's default "An unexpected error occurred" (detailed errors are
	/// off) reached the user instead of the provider's real message, silently defeating
	/// EventModal.tsx's and ReadingPane.tsx's InviteBanner's error reporting.
	/// </summary>
	[Fact]
	public async Task A_provider_rejection_on_create_reaches_the_caller_as_a_HubException_with_the_real_message()
	{
		var provider = new ScriptedCalendarProvider { RejectNextCreateWith = new InvalidOperationException("Quota exceeded.") };
		await using var harness = await Harness.CreateAsync(provider);

		var ex = await Assert.ThrowsAsync<HubException>(
			() => harness.UsingAsync(scope =>
				scope.GetRequiredService<CalendarEventService>().SaveAsync(
					new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
				)
			)
		);
		Assert.Equal("Quota exceeded.", ex.Message);

		await harness.UsingAsync(async scope =>
		{
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.ToListAsync());
		});
	}

	/// <summary>
	/// Seventy-sixth pass: same bug shape as passes 71-75 — a client-supplied EventId was
	/// resolved with no check it belongs to the client-supplied CalendarId. Left unchecked,
	/// SaveAsync would mutate a foreign calendar's event and push the edit through the wrong
	/// account's provider credentials, since it resolves <c>account</c>/<c>provider</c> from
	/// <c>input.CalendarId</c> but the mutated row from <c>input.EventId</c> alone.
	/// </summary>
	[Fact]
	public async Task Saving_an_event_id_that_belongs_to_a_different_calendar_is_rejected()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);
		var created = await harness.UsingAsync(scope =>
			scope.GetRequiredService<CalendarEventService>().SaveAsync(
				new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
			)
		);

		var otherCalendarId = await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var otherAccount = new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap };
			var otherCalendar = new Calendar
			{
				Id = Guid.NewGuid(),
				AccountId = otherAccount.Id,
				ProviderCalendarId = "other-cal",
				Name = "Other Calendar",
			};
			context.Accounts.Add(otherAccount);
			context.Calendars.Add(otherCalendar);
			await context.SaveChangesAsync();
			return otherCalendar.Id;
		});

		var ex = await Assert.ThrowsAsync<HubException>(
			() => harness.UsingAsync(scope =>
				scope.GetRequiredService<CalendarEventService>().SaveAsync(
					new CalendarEventInput(created.Id, otherCalendarId, "Hijacked", null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), false)
				)
			)
		);
		Assert.Contains(created.Id.ToString(), ex.Message);

		await harness.UsingAsync(async scope =>
		{
			var row = await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.SingleAsync(e => e.Id == created.Id);
			Assert.Equal("Standup", row.Title);
			Assert.Equal(harness.CalendarId, row.CalendarId);
		});
	}

	/// <summary>
	/// Two-hundred-and-twenty-seventh pass: EventModal.tsx's Start/End fields are plain text
	/// inputs with no min/max tying one to the other, so nothing stopped a direct
	/// SaveCalendarEvent call (or a client bug) from creating an event whose End is before its
	/// Start — a shape RFC 5545 has no way to express, which CalDavIcs.RenderVEvent would have
	/// turned into a malformed resource rather than a clean rejection.
	/// </summary>
	[Fact]
	public async Task Saving_an_event_that_ends_before_it_starts_is_rejected()
	{
		var provider = new ScriptedCalendarProvider();
		await using var harness = await Harness.CreateAsync(provider);

		var ex = await Assert.ThrowsAsync<HubException>(
			() => harness.UsingAsync(scope =>
				scope.GetRequiredService<CalendarEventService>().SaveAsync(
					new CalendarEventInput(null, harness.CalendarId, "Standup", null, null, DateTimeOffset.UnixEpoch.AddHours(1), DateTimeOffset.UnixEpoch, false)
				)
			)
		);
		Assert.Equal("An event cannot end before it starts.", ex.Message);

		await harness.UsingAsync(async scope =>
		{
			Assert.Empty(await scope.GetRequiredService<MyloMailDbContext>().CalendarEvents.ToListAsync());
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
		private CalendarSyncResult? scriptedSync;

		public int CreateCalls { get; private set; }
		public int DeleteCalls { get; private set; }
		public bool RejectNextUpdate { get; set; }
		public bool RejectNextDelete { get; set; }
		public Exception? RejectNextCreateWith { get; set; }

		/// <summary>The <c>expectedETag</c> the most recent <see cref="UpdateEventAsync"/> call received.</summary>
		public string? LastUpdateETag { get; private set; }

		public ProviderType Type => ProviderType.Imap;

		/// <summary>What <see cref="SyncCalendarAsync"/> returns on its next call — "the server's" state.</summary>
		public void ScriptSync(CalendarSyncResult result) => scriptedSync = result;

		public Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct) =>
			Task.FromResult<IReadOnlyList<CalendarDto>>([new CalendarDto("cal", "Calendar", null, true)]);

		public Task<CalendarSyncResult> SyncCalendarAsync(Account account, Calendar calendar, string? cursor, string? continuation, CancellationToken ct)
		{
			var result = scriptedSync ?? new CalendarSyncResult(cursor, null, [], []);
			scriptedSync = null;
			return Task.FromResult(result);
		}

		public Task<CalendarEventCreation> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct)
		{
			if (RejectNextCreateWith is Exception ex)
			{
				RejectNextCreateWith = null;
				throw ex;
			}
			CreateCalls++;
			return Task.FromResult(new CalendarEventCreation($"created-{CreateCalls}", "created-revision"));
		}

		public Task<CalendarEventDto?> FindEventAsync(
			Account account,
			Calendar calendar,
			string stableICalUid,
			string providerCreationKey,
			CancellationToken ct
		) => Task.FromResult<CalendarEventDto?>(null);

		public Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct)
		{
			LastUpdateETag = expectedETag;
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

		public Task RespondToInviteAsync(Account account, CalendarEvent ev, InviteResponse response, string? comment, Address replyingAs, CancellationToken ct) =>
			throw new NotSupportedException();
	}
}
