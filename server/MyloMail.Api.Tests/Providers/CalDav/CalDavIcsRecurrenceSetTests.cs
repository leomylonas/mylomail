using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-twenty-fifth architecture-review pass: <c>RDATE</c>/<c>EXDATE</c> were always
/// rendered as bare UTC values, even when <c>DTSTART</c> carried a <c>TZID</c> — RFC 5545
/// §3.8.5.1/§3.8.5.2 require an EXDATE/RDATE to match DTSTART's exact value type and time zone,
/// since a receiving client compares them literally against the instances it generates from
/// <c>RRULE</c> in DTSTART's own zone. A mismatched EXDATE simply fails to exclude anything.
/// </summary>
public sealed class CalDavIcsRecurrenceSetTests
{
	[Fact]
	public void An_EXDATE_on_a_zoned_event_carries_the_same_TZID_as_DTSTART()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "series-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(-4)),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.FromHours(-4)),
			StartTimeZoneId = "America/New_York",
			EndTimeZoneId = "America/New_York",
			RecurrenceRules = ["FREQ=WEEKLY"],
			ExceptionDates = [new DateTimeOffset(2026, 3, 17, 9, 0, 0, TimeSpan.FromHours(-4))],
		};

		var ics = CalDavIcs.ToIcs("series-1", ev);

		Assert.Contains("EXDATE;TZID=America/New_York:20260317T090000", ics);
		Assert.DoesNotContain("EXDATE:", ics);
	}

	/// <summary>
	/// A follow-up gap `invariant-review` found in this same pass: <c>ParseEvents</c> discarded
	/// each RDATE/EXDATE occurrence's own params and parsed with <c>NoParams</c> — a zoned value
	/// this fix now writes (<c>EXDATE;TZID=...</c>) would round-trip back through MyloMail's own
	/// reader as if it were UTC, several hours off. Confirms a write-then-read round trip lands
	/// on the exact same instant.
	/// </summary>
	[Fact]
	public void An_EXDATE_round_trips_through_the_reader_at_the_same_instant()
	{
		var exceptionInstant = new DateTimeOffset(2026, 3, 17, 9, 0, 0, TimeSpan.FromHours(-4));
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "series-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.FromHours(-4)),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.FromHours(-4)),
			StartTimeZoneId = "America/New_York",
			EndTimeZoneId = "America/New_York",
			RecurrenceRules = ["FREQ=WEEKLY"],
			ExceptionDates = [exceptionInstant],
		};

		var ics = CalDavIcs.ToIcs("series-1", ev);
		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var roundTripped = Assert.Single(parsed.ExceptionDates);
		Assert.Equal(exceptionInstant.ToUniversalTime(), roundTripped.ToUniversalTime());
	}

	[Fact]
	public void An_RDATE_on_an_all_day_event_uses_VALUE_DATE_like_DTSTART()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "series-1",
			Title = "Holiday",
			Start = new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero),
			IsAllDay = true,
			RecurrenceRules = ["FREQ=YEARLY"],
			RecurrenceDates = [new DateTimeOffset(2027, 3, 10, 0, 0, 0, TimeSpan.Zero)],
		};

		var ics = CalDavIcs.ToIcs("series-1", ev);

		Assert.Contains("RDATE;VALUE=DATE:20270310", ics);
	}
}
