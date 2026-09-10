using Microsoft.Graph.Models;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Graph;
using Xunit;
using DomainAttendee = MyloMail.Api.Domain.Attendee;
using DomainResponseStatus = MyloMail.Api.Domain.ResponseStatus;
using GraphAttendee = Microsoft.Graph.Models.Attendee;
using GraphDayOfWeek = Microsoft.Graph.Models.DayOfWeekObject;
using GraphEvent = Microsoft.Graph.Models.Event;
using GraphResponseStatus = Microsoft.Graph.Models.ResponseStatus;
namespace MyloMail.Api.Tests.Providers;

public sealed class GraphCalendarProviderMappingTests
{
	[Fact]
	public void Maps_native_event_identity_revision_and_calendar_fields()
	{
		var graphEvent = new GraphEvent
		{
			Id = "immutable-event-id",
			ICalUId = "ical-uid",
			Subject = "Planning",
			Body = new ItemBody { Content = "<p>Discuss</p>" },
			Location = new Location { DisplayName = "Room 2" },
			Start = new DateTimeTimeZone { DateTime = "2026-04-01T09:00:00", TimeZone = "UTC" },
			End = new DateTimeTimeZone { DateTime = "2026-04-01T10:00:00", TimeZone = "UTC" },
			IsAllDay = false,
			ShowAs = FreeBusyStatus.Tentative,
			Organizer = new Recipient { EmailAddress = new EmailAddress { Name = "Organizer", Address = "organizer@example.test" } },
			Attendees =
			[
				new GraphAttendee
				{
					EmailAddress = new EmailAddress { Name = "Required", Address = "required@example.test" },
					Type = AttendeeType.Required,
					Status = new GraphResponseStatus { Response = ResponseType.Accepted },
				},
			],
			IsReminderOn = true,
			ReminderMinutesBeforeStart = 15,
			AdditionalData = new Dictionary<string, object> { ["@odata.etag"] = "W/\"revision\"" },
		};

		var mapped = GraphCalendarProvider.ToDto(graphEvent);

		Assert.Equal("immutable-event-id", mapped.ProviderEventId);
		Assert.Equal("ical-uid", mapped.ICalUid);
		Assert.Equal("W/\"revision\"", mapped.ProviderRevision);
		Assert.Equal("Planning", mapped.Title);
		Assert.Equal("Room 2", mapped.Location);
		Assert.Equal("<p>Discuss</p>", mapped.Description);
		Assert.Equal("Etc/UTC", mapped.StartTimeZoneId);
		Assert.Equal(EventStatus.Tentative, mapped.Status);
		Assert.Equal(new Address("Organizer", "organizer@example.test"), mapped.Organizer);
		Assert.Equal(new DateTimeOffset(2026, 4, 1, 8, 45, 0, TimeSpan.Zero), Assert.Single(mapped.Reminders));
		Assert.Equal(new DomainAttendee("Required", "required@example.test", AttendeeRole.Required, DomainResponseStatus.Accepted), Assert.Single(mapped.Attendees));
	}

	[Fact]
	public void Maps_a_native_weekly_recurrence_to_a_portable_rule()
	{
		var graphEvent = new GraphEvent
		{
			Id = "immutable-master-id",
			ICalUId = "master-ical-uid",
			Start = new DateTimeTimeZone { DateTime = "2026-04-06T09:00:00", TimeZone = "UTC" },
			End = new DateTimeTimeZone { DateTime = "2026-04-06T10:00:00", TimeZone = "UTC" },
			Recurrence = new PatternedRecurrence
			{
				Pattern = new RecurrencePattern
				{
					Type = RecurrencePatternType.Weekly,
					Interval = 2,
					DaysOfWeek = [GraphDayOfWeek.Monday, GraphDayOfWeek.Wednesday],
				},
				Range = new RecurrenceRange { Type = RecurrenceRangeType.NoEnd },
			},
		};

		var mapped = GraphCalendarProvider.ToDto(graphEvent);

		Assert.Equal(["FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE"], mapped.RecurrenceRules);
	}

	[Fact]
	public void Maps_override_to_series_master_and_original_occurrence_start()
	{
		var graphEvent = new GraphEvent
		{
			Id = "immutable-override-id",
			ICalUId = "override-ical-uid",
			SeriesMasterId = "immutable-master-id",
			OriginalStart = new DateTimeOffset(2026, 4, 8, 9, 0, 0, TimeSpan.Zero),
			Start = new DateTimeTimeZone { DateTime = "2026-04-08T11:00:00", TimeZone = "UTC" },
			End = new DateTimeTimeZone { DateTime = "2026-04-08T12:00:00", TimeZone = "UTC" },
		};

		var mapped = GraphCalendarProvider.ToDto(graphEvent);

		Assert.Equal("immutable-override-id", mapped.ProviderEventId);
		Assert.Equal("immutable-master-id", mapped.RecurrenceMasterProviderEventId);
		Assert.Equal(new DateTimeOffset(2026, 4, 8, 9, 0, 0, TimeSpan.Zero), mapped.RecurrenceId);
	}
	[Fact]
	public void Converts_a_Graph_Windows_timezone_before_deriving_the_event_instant()
	{
		var mapped = GraphCalendarProvider.ToDto(new GraphEvent
		{
			Id = "timezone-event",
			Start = new DateTimeTimeZone { DateTime = "2026-07-01T09:00:00", TimeZone = "Eastern Standard Time" },
			End = new DateTimeTimeZone { DateTime = "2026-07-01T10:00:00", TimeZone = "Eastern Standard Time" },
		});

		Assert.Equal("America/New_York", mapped.StartTimeZoneId);
		Assert.Equal(new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.FromHours(-4)), mapped.Start);
	}
}

