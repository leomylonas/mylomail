using MyloMail.Api.Providers.CalDav;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Two-hundred-and-thirty-second architecture-review pass: a TZID this runtime's own
/// <see cref="TimeZoneInfo"/> database can't resolve (a legacy Windows-style id from an older
/// client, say) was previously stored on <c>CalendarEventDto</c> verbatim even though
/// <c>ParseDateTime</c> already falls back to treating the value as floating/UTC in that case.
/// A later <c>RenderVEvent</c> (any edit re-saving the event) would then write that same
/// unresolvable id straight back into a <c>TZID=</c> parameter while <c>FormatLocal</c> silently
/// wrote the raw UTC instant under it — a resource that declares a zone the value was never
/// actually converted into.
/// </summary>
public sealed class CalDavIcsUnknownTimeZoneTests
{
	/// <summary>
	/// Confirmed as a genuine discriminator: reverting <c>KnownTimeZoneId</c> to pass the raw
	/// TZID straight through makes this fail — <c>StartTimeZoneId</c> comes back as the
	/// unresolvable "Nonexistent/Zone" instead of null.
	/// </summary>
	[Fact]
	public void An_unresolvable_time_zone_id_is_not_stored_on_the_parsed_event()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:event-1\r\n"
			+ "DTSTART;TZID=Nonexistent/Zone:20260310T090000\r\n"
			+ "DTEND;TZID=Nonexistent/Zone:20260310T100000\r\n"
			+ "SUMMARY:Standup\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Null(parsed.StartTimeZoneId);
		Assert.Null(parsed.EndTimeZoneId);
	}

	/// <summary>A known, real IANA id is unaffected — still round-trips as-is.</summary>
	[Fact]
	public void A_resolvable_time_zone_id_is_kept()
	{
		var ics =
			"BEGIN:VCALENDAR\r\n"
			+ "VERSION:2.0\r\n"
			+ "BEGIN:VEVENT\r\n"
			+ "UID:event-1\r\n"
			+ "DTSTART;TZID=America/New_York:20260310T090000\r\n"
			+ "DTEND;TZID=America/New_York:20260310T100000\r\n"
			+ "SUMMARY:Standup\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal("America/New_York", parsed.StartTimeZoneId);
		Assert.Equal("America/New_York", parsed.EndTimeZoneId);
	}
}
