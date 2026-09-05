using MyloMail.Api.Providers.CalDav;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-ninety-second architecture-review pass: a duration-relative <c>VALARM</c>
/// <c>TRIGGER</c> defaults to <c>RELATED=START</c> per RFC 5545 §3.8.6.3, but a server may
/// explicitly send <c>RELATED=END</c> — anchoring every duration-relative trigger to the event's
/// start unconditionally silently fired such a reminder at the wrong instant whenever an event's
/// end differs from its start.
/// </summary>
public sealed class CalDavIcsTriggerRelatedTests
{
	/// <summary>
	/// Confirmed as a genuine discriminator: reverting the RELATED-aware anchor selection back to
	/// always using <c>start</c> makes this fail (computing 08:45 instead of the expected 09:15).
	/// </summary>
	[Fact]
	public void A_trigger_related_to_the_events_end_is_anchored_to_end_not_start()
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
			+ "TRIGGER;RELATED=END:-PT15M\r\n"
			+ "END:VALARM\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var reminder = Assert.Single(parsed.Reminders);
		Assert.Equal(new DateTimeOffset(2026, 3, 10, 9, 15, 0, TimeSpan.Zero), reminder);
	}

	/// <summary>
	/// invariant-review's finding in this same pass: parameter VALUEs (unlike parameter NAMEs)
	/// are never case-normalized by <c>ParseLine</c>, so a naive case-sensitive comparison here
	/// would silently miss a real server sending lowercase <c>related=end</c>.
	/// </summary>
	[Fact]
	public void A_trigger_related_to_end_is_recognised_case_insensitively()
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
			+ "TRIGGER;related=end:-PT15M\r\n"
			+ "END:VALARM\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var reminder = Assert.Single(parsed.Reminders);
		Assert.Equal(new DateTimeOffset(2026, 3, 10, 9, 15, 0, TimeSpan.Zero), reminder);
	}

	/// <summary>The already-covered default: no RELATED param means relative to start.</summary>
	[Fact]
	public void A_trigger_with_no_related_param_is_anchored_to_start()
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
			+ "TRIGGER:-PT15M\r\n"
			+ "END:VALARM\r\n"
			+ "END:VEVENT\r\n"
			+ "END:VCALENDAR\r\n";

		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var reminder = Assert.Single(parsed.Reminders);
		Assert.Equal(new DateTimeOffset(2026, 3, 10, 8, 45, 0, TimeSpan.Zero), reminder);
	}
}
