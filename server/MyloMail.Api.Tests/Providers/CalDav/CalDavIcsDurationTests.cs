using MyloMail.Api.Providers.CalDav;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-ninetieth architecture-review pass: RFC 5545 §3.6.1 permits a VEVENT to carry
/// either DTEND or DURATION (never both) — <see cref="CalDavIcs.ParseEvents"/> only ever read
/// DTEND, silently falling back to a fixed one-hour default whenever a real server or another
/// client (common for all-day and templated recurring events) emitted DURATION instead.
/// </summary>
public sealed class CalDavIcsDurationTests
{
	[Fact]
	public void A_vevent_with_duration_instead_of_dtend_computes_the_correct_end()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:uid-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "DURATION:PT1H30M\r\n"
			+ "SUMMARY:Standup\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal(new DateTimeOffset(2026, 3, 10, 10, 30, 0, TimeSpan.Zero), parsed.End);
	}

	[Fact]
	public void A_vevent_with_a_multi_day_duration_and_no_dtend_computes_the_correct_end()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:uid-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "DURATION:P2DT3H\r\n"
			+ "SUMMARY:Offsite\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal(new DateTimeOffset(2026, 3, 12, 12, 0, 0, TimeSpan.Zero), parsed.End);
	}

	[Fact]
	public void An_all_day_vevent_with_neither_dtend_nor_duration_defaults_to_one_day()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:uid-1\r\n"
			+ "DTSTART;VALUE=DATE:20260310\r\n"
			+ "SUMMARY:Holiday\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal(new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero), parsed.End);
	}

	[Fact]
	public void A_timed_vevent_with_neither_dtend_nor_duration_defaults_to_zero_length()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:uid-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "SUMMARY:Reminder\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal(parsed.Start, parsed.End);
	}
}
