using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// User-approved feature, following a forty-eighth-pass finding: recurring events never
/// actually recurred in the UI. These cover the override/cancellation precedence a generated
/// occurrence must respect (§13 Epic 7, §15's "detect, don't silently merge" spirit).
/// </summary>
public sealed class CalendarEventOccurrencesTests
{
	[Fact]
	public async Task A_modified_override_replaces_the_generated_occurrence_at_its_original_slot()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (calendarId, masterId, secondOccurrence) = await SeedWeeklySeriesAsync(database);

		await UsingAsync(
			database,
			async context =>
			{
				context.CalendarEvents.Add(
					new CalendarEvent
					{
						Id = Guid.NewGuid(),
						CalendarId = calendarId,
						Title = "Standup (moved to the afternoon)",
						ProviderEventId = "override-1",
						Start = secondOccurrence.AddHours(6),
						End = secondOccurrence.AddHours(6.5),
						RecurrenceMasterId = masterId,
						RecurrenceId = secondOccurrence,
						Status = EventStatus.Confirmed,
					}
				);
				await context.SaveChangesAsync();
				return true;
			}
		);

		var events = await UsingAsync(
			database,
			context => CalendarEventOccurrences.ForCalendarAsync(
				context,
				calendarId,
				secondOccurrence.AddDays(-1),
				secondOccurrence.AddDays(1)
			)
		);

		var atThatDay = Assert.Single(events);
		Assert.False(atThatDay.IsVirtualOccurrence);
		Assert.Equal("Standup (moved to the afternoon)", atThatDay.Title);
		Assert.Equal(secondOccurrence.AddHours(6), atThatDay.Start);
	}

	/// <summary>
	/// A cancelled instance is a real, already-materialised row — whether the calendar view
	/// greys it out or hides it is existing, unrelated rendering behaviour this feature
	/// doesn't change. What this feature must guarantee is that no *second*, generated
	/// occurrence also appears at the slot the cancellation vacated.
	/// </summary>
	[Fact]
	public async Task A_cancelled_instance_is_not_duplicated_by_a_generated_ghost_occurrence()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (calendarId, masterId, secondOccurrence) = await SeedWeeklySeriesAsync(database);

		await UsingAsync(
			database,
			async context =>
			{
				context.CalendarEvents.Add(
					new CalendarEvent
					{
						Id = Guid.NewGuid(),
						CalendarId = calendarId,
						Title = "Standup",
						ProviderEventId = "override-1",
						Start = secondOccurrence,
						End = secondOccurrence.AddMinutes(30),
						RecurrenceMasterId = masterId,
						RecurrenceId = secondOccurrence,
						Status = EventStatus.Cancelled,
					}
				);
				await context.SaveChangesAsync();
				return true;
			}
		);

		var events = await UsingAsync(
			database,
			context => CalendarEventOccurrences.ForCalendarAsync(
				context,
				calendarId,
				secondOccurrence.AddDays(-1),
				secondOccurrence.AddDays(1)
			)
		);

		// Exactly the one real, cancelled row — never a second, generated occurrence at the
		// same original slot.
		var atThatDay = Assert.Single(events);
		Assert.False(atThatDay.IsVirtualOccurrence);
		Assert.Equal(EventStatus.Cancelled, atThatDay.Status);
	}

	[Fact]
	public async Task An_unmodified_occurrence_is_synthesised_as_a_virtual_row()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (calendarId, masterId, secondOccurrence) = await SeedWeeklySeriesAsync(database);

		var events = await UsingAsync(
			database,
			context => CalendarEventOccurrences.ForCalendarAsync(
				context,
				calendarId,
				secondOccurrence.AddDays(-1),
				secondOccurrence.AddDays(1)
			)
		);

		var occurrence = Assert.Single(events);
		Assert.True(occurrence.IsVirtualOccurrence);
		Assert.Equal(masterId, occurrence.MasterEventId);
		Assert.Equal(secondOccurrence, occurrence.Start);
		Assert.Equal("Standup", occurrence.Title);
	}

	private static async Task<(Guid CalendarId, Guid MasterId, DateTimeOffset SecondOccurrence)> SeedWeeklySeriesAsync(
		TestDatabase database
	)
	{
		var accountId = Guid.NewGuid();
		var calendarId = Guid.NewGuid();
		var masterId = Guid.NewGuid();
		var firstOccurrence = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
		var secondOccurrence = firstOccurrence.AddDays(7);

		await UsingAsync(
			database,
			async context =>
			{
				context.Accounts.Add(new Account { Id = accountId, ProviderType = ProviderType.Imap });
				context.Calendars.Add(
					new Calendar
					{
						Id = calendarId,
						AccountId = accountId,
						ProviderCalendarId = "calendar-1",
						Name = "Calendar",
					}
				);
				context.CalendarEvents.Add(
					new CalendarEvent
					{
						Id = masterId,
						CalendarId = calendarId,
						Title = "Standup",
						ProviderEventId = "master-1",
						Start = firstOccurrence,
						End = firstOccurrence.AddMinutes(30),
						RecurrenceRules = ["FREQ=WEEKLY;COUNT=6"],
						Status = EventStatus.Confirmed,
					}
				);
				await context.SaveChangesAsync();
				return true;
			}
		);

		return (calendarId, masterId, secondOccurrence);
	}

	private static async Task<T> UsingAsync<T>(TestDatabase database, Func<MyloMailDbContext, Task<T>> work)
	{
		await using var scope = database.CreateScope();
		return await work(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>());
	}
}
