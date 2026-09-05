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

	/// <summary>
	/// Pass 195: RFC 5234 §2.3 makes the P/T/D/W/H/M/S dur-value designators (§3.3.6)
	/// case-insensitive quoted literals, the same convention already fixed for parameter-value
	/// tokens and the UTC 'Z' suffix elsewhere in this file. <c>TryParseDuration</c> compared them
	/// case-sensitively, so a lowercase duration (e.g. "-pt15m", produced here for DURATION rather
	/// than TRIGGER since both go through the same helper) silently returned zero contribution for
	/// every mismatched unit letter instead of the actual duration. Confirmed as a genuine
	/// discriminator — reverting the case-normalization in <c>TryParseDuration</c> makes this fail
	/// by computing an end equal to start (the lowercase "1h30m" contributes nothing) instead of
	/// 90 minutes later.
	/// </summary>
	[Fact]
	public void A_lowercase_duration_designator_is_recognised()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:uid-1\r\n"
			+ "DTSTART:20260310T090000Z\r\n"
			+ "DURATION:pt1h30m\r\n"
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
