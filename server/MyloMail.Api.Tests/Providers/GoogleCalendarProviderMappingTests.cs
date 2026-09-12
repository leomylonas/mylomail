using Google.Apis.Calendar.v3.Data;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using Xunit;
using GoogleCalendarEvent = Google.Apis.Calendar.v3.Data.Event;

namespace MyloMail.Api.Tests.Providers;

public sealed class GoogleCalendarProviderMappingTests
{
	[Fact]
	public void Maps_a_recurrence_master_without_losing_calendar_event_metadata()
	{
		var dto = GoogleCalendarProvider.ToDto("work@example.test", new GoogleCalendarEvent
		{
			Id = "event-1",
			ICalUID = "uid-1",
			ETag = "etag-1",
			Sequence = 4,
			Summary = "Planning",
			Location = "Room 4",
			Description = "Discuss milestones",
			Start = Timed("2026-03-01T14:00:00Z", "Europe/London"),
			End = Timed("2026-03-01T15:00:00Z", "Europe/London"),
			Organizer = new Event.OrganizerData { DisplayName = "Owner", Email = "owner@example.test" },
			Attendees =
			[
				new EventAttendee { DisplayName = "Required", Email = "required@example.test", ResponseStatus = "accepted" },
				new EventAttendee { DisplayName = "Optional", Email = "optional@example.test", Optional = true, ResponseStatus = "tentative" },
				new EventAttendee { DisplayName = "Room", Email = "room@example.test", Resource = true, ResponseStatus = "needsAction" },
			],
			Reminders = new Event.RemindersData
			{
				UseDefault = false,
				Overrides = [new EventReminder { Method = "popup", Minutes = 15 }],
			},
			Status = "tentative",
			Recurrence =
			[
				"RRULE:FREQ=WEEKLY;COUNT=3",
				"RDATE:20260308T140000Z,20260315T140000Z",
				"EXDATE:20260322T140000Z",
			],
		});

		Assert.StartsWith("gcal:", dto.ProviderEventId);
		Assert.Equal("uid-1", dto.ICalUid);
		Assert.Equal("etag-1", dto.ProviderRevision);
		Assert.Equal(4, dto.Sequence);
		Assert.Equal("Planning", dto.Title);
		Assert.Equal("Room 4", dto.Location);
		Assert.Equal("Discuss milestones", dto.Description);
		Assert.Equal(new DateTimeOffset(2026, 3, 1, 14, 0, 0, TimeSpan.Zero), dto.Start);
		Assert.Equal("Europe/London", dto.StartTimeZoneId);
		Assert.Equal(new Address("Owner", "owner@example.test"), dto.Organizer);
		Assert.Equal(EventStatus.Tentative, dto.Status);
		Assert.Equal(
			[
				("Required", "required@example.test", AttendeeRole.Required, ResponseStatus.Accepted),
				("Optional", "optional@example.test", AttendeeRole.Optional, ResponseStatus.Tentative),
				("Room", "room@example.test", AttendeeRole.Resource, ResponseStatus.NeedsAction),
			],
			dto.Attendees.Select(a => (a.Name, a.Email, a.Role, a.ResponseStatus))
		);
		Assert.Equal(["FREQ=WEEKLY;COUNT=3"], dto.RecurrenceRules);
		Assert.Equal(
			[
				new DateTimeOffset(2026, 3, 8, 14, 0, 0, TimeSpan.Zero),
				new DateTimeOffset(2026, 3, 15, 14, 0, 0, TimeSpan.Zero),
			],
			dto.RecurrenceDates
		);
		Assert.Equal([new DateTimeOffset(2026, 3, 1, 13, 45, 0, TimeSpan.Zero)], dto.Reminders);
		Assert.Equal([new DateTimeOffset(2026, 3, 22, 14, 0, 0, TimeSpan.Zero)], dto.ExceptionDates);
		Assert.Null(dto.RecurrenceMasterProviderEventId);
		Assert.Null(dto.RecurrenceId);
	}

	[Fact]
	public void Maps_an_override_to_a_stable_master_and_original_start_reference()
	{
		var dto = GoogleCalendarProvider.ToDto("work@example.test", new GoogleCalendarEvent
		{
			Id = "instance-id-that-must-not-be-persisted-as-the-address",
			ICalUID = "series-uid",
			RecurringEventId = "series-master",
			OriginalStartTime = Timed("2026-03-08T14:00:00Z", "Europe/London"),
			Start = Timed("2026-03-08T16:00:00Z", "Europe/London"),
			End = Timed("2026-03-08T17:00:00Z", "Europe/London"),
		});

		Assert.StartsWith("gcal:", dto.ProviderEventId);
		Assert.DoesNotContain("instance-id-that-must-not-be-persisted-as-the-address", dto.ProviderEventId);
		Assert.StartsWith("gcal:", dto.RecurrenceMasterProviderEventId!);
		Assert.Equal(new DateTimeOffset(2026, 3, 8, 14, 0, 0, TimeSpan.Zero), dto.RecurrenceId);
		Assert.Equal(new DateTimeOffset(2026, 3, 8, 16, 0, 0, TimeSpan.Zero), dto.Start);
	}

	[Fact]
	public void Preserves_local_recurrence_times_and_date_only_values()
	{
		var timed = GoogleCalendarProvider.ToDto("primary", new GoogleCalendarEvent
		{
			Id = "timed",
			Start = Timed("2026-03-01T09:00:00-05:00", "America/New_York"),
			End = Timed("2026-03-01T10:00:00-05:00", "America/New_York"),
			Recurrence = ["RDATE;TZID=America/New_York:20260308T090000"],
		});
		Assert.Equal(new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.FromHours(-4)), Assert.Single(timed.RecurrenceDates));

		var allDay = GoogleCalendarProvider.ToGoogleEvent(new CalendarEventDto
		{
			ProviderEventId = "event",
			ICalUid = "uid",
			Start = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero),
			IsAllDay = true,
			RecurrenceDates = [new DateTimeOffset(2026, 4, 8, 0, 0, 0, TimeSpan.Zero)],
		});
		Assert.Equal(["RDATE;VALUE=DATE:20260408"], allDay.Recurrence);
	}

	[Fact]
	public void Maps_date_only_events_as_all_day_at_utc_midnight()
	{
		var dto = GoogleCalendarProvider.ToDto("primary", new GoogleCalendarEvent
		{
			Id = "all-day",
			Start = new EventDateTime { Date = "2026-04-01" },
			End = new EventDateTime { Date = "2026-04-02" },
		});

		Assert.True(dto.IsAllDay);
		Assert.Equal(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero), dto.Start);
		Assert.Equal(new DateTimeOffset(2026, 4, 2, 0, 0, 0, TimeSpan.Zero), dto.End);
	}

	private static EventDateTime Timed(string value, string timeZone) => new()
	{
		DateTimeDateTimeOffset = DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal),
		TimeZone = timeZone,
	};
}
