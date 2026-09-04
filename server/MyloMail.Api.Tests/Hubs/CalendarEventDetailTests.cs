using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// Ninety-eighth architecture-review pass: <see cref="MailHub.GetCalendarEventDetail"/> did not
/// report the row's own Start/End/IsAllDay at all. Editing a not-yet-materialised virtual
/// occurrence routes to its master by id (§13 Epic 7's deferred per-occurrence editing), and
/// <c>EventModal.tsx</c> pre-filled the edit form with the clicked occurrence's own derived
/// date rather than the master's real start — saving without touching the date fields would
/// silently reschedule the whole series to that occurrence's date, since
/// <c>CalendarEventService.UpdateAsync</c> unconditionally overwrites Start/End with whatever
/// the form holds. Adding these fields lets the form correct itself to the master's real dates
/// once the detail query resolves.
/// </summary>
public sealed class CalendarEventDetailTests
{
	[Fact]
	public async Task Detail_reports_the_rows_own_start_and_end_not_any_occurrences()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var masterStart = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
		var eventId = await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await harness.AccountInScopeAsync(services);
			context.SendIdentities.Add(
				new SendIdentity
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					EmailAddress = "me@example.test",
					IsDefault = true,
				}
			);
			var calendarId = Guid.NewGuid();
			context.Calendars.Add(
				new Calendar
				{
					Id = calendarId,
					AccountId = account.Id,
					ProviderCalendarId = "cal",
					Name = "Calendar",
				}
			);
			var id = Guid.NewGuid();
			context.CalendarEvents.Add(
				new CalendarEvent
				{
					Id = id,
					CalendarId = calendarId,
					Title = "Standup",
					ProviderEventId = "master-1",
					Start = masterStart,
					End = masterStart.AddMinutes(30),
					Status = EventStatus.Confirmed,
					RecurrenceRules = ["FREQ=WEEKLY"],
				}
			);
			await context.SaveChangesAsync();
			return id;
		});

		var detail = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().GetCalendarEventDetail(eventId)
		);

		// Deliberately not the third occurrence's own date (masterStart plus multiple weeks) —
		// the master's real Start is what a save must be measured against, since a virtual
		// occurrence's edit is routed to this same row by id.
		Assert.Equal(masterStart, detail.Start);
		Assert.Equal(masterStart.AddMinutes(30), detail.End);
		Assert.False(detail.IsAllDay);
	}
}
