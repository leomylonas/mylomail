using MyloMail.Api.Domain;
using MyloMail.Api.Providers.CalDav;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-ninety-third architecture-review pass: continuing 192's finding that
/// <c>ParseLine</c> never case-normalizes parameter *values* (only names) — every other
/// case-sensitive comparison against a known RFC 5545 parameter-value token (<c>VALUE</c>,
/// <c>ROLE</c>, <c>PARTSTAT</c>, <c>STATUS</c>) shares 192's RELATED=END bug shape, since these
/// are all ABNF terminal strings and therefore case-insensitive per RFC 5234 §2.3.
/// </summary>
public sealed class CalDavIcsParamCaseTests
{
	/// <summary>
	/// Confirmed as a genuine discriminator: reverting the DTSTART VALUE=DATE comparison to
	/// case-sensitive makes this fail — a lowercase "value=date" would be read as a timed
	/// DTSTART with no "T" separator, throwing a FormatException out of ParseDateTime instead
	/// of parsing as an all-day event.
	/// </summary>
	[Fact]
	public void An_all_day_event_is_recognised_when_the_server_sends_a_lowercase_value_param()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:event-1\r\n"
			+ "DTSTART;value=date:20260310\r\n"
			+ "DTEND;value=date:20260311\r\n"
			+ "SUMMARY:Conference\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.True(parsed.IsAllDay);
		Assert.Equal(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), parsed.Start);
	}

	/// <summary>
	/// Confirmed as a genuine discriminator: reverting the TRIGGER VALUE=DATE-TIME comparison to
	/// case-sensitive makes this fail — a lowercase "value=date-time" TRIGGER would fall through
	/// to the duration parser, which rejects an absolute date-time value outright (it does not
	/// start with 'P'), silently dropping the reminder instead of reading its absolute instant.
	/// </summary>
	[Fact]
	public void An_absolute_trigger_is_recognised_when_the_server_sends_a_lowercase_value_param()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:event-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "DTEND:20260310T093000Z\r\n"
			+ "SUMMARY:Standup\r\n"
			+ "BEGIN:VALARM\r\n"
			+ "ACTION:DISPLAY\r\n"
			+ "TRIGGER;value=date-time:20260310T083000Z\r\n"
			+ "END:VALARM\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var reminder = Assert.Single(parsed.Reminders);
		Assert.Equal(new DateTimeOffset(2026, 3, 10, 8, 30, 0, TimeSpan.Zero), reminder);
	}

	/// <summary>
	/// Confirmed as a genuine discriminator: reverting ROLE to a case-sensitive switch makes this
	/// fail — a lowercase "role=opt-participant" attendee would silently be read as Required
	/// instead of Optional.
	/// </summary>
	[Fact]
	public void An_attendees_role_is_recognised_when_the_server_sends_it_lowercase()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:event-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "DTEND:20260310T093000Z\r\n"
			+ "SUMMARY:Standup\r\n"
			+ "ATTENDEE;role=opt-participant;partstat=tentative:mailto:a@example.com\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var attendee = Assert.Single(parsed.Attendees);
		Assert.Equal(AttendeeRole.Optional, attendee.Role);
		Assert.Equal(ResponseStatus.Tentative, attendee.ResponseStatus);
	}

	/// <summary>
	/// Confirmed as a genuine discriminator: reverting STATUS to a case-sensitive switch makes
	/// this fail — a lowercase "cancelled" status would silently be read as Confirmed, hiding a
	/// real cancellation from the user.
	/// </summary>
	[Fact]
	public void An_events_status_is_recognised_when_the_server_sends_it_lowercase()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:event-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "DTEND:20260310T093000Z\r\n"
			+ "SUMMARY:Standup\r\n"
			+ "STATUS:cancelled\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal(EventStatus.Cancelled, parsed.Status);
	}
}
