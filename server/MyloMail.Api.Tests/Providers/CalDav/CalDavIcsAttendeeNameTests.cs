using MyloMail.Api.Domain;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-twenty-sixth architecture-review pass: <c>ORGANIZER</c>/<c>ATTENDEE</c>'s
/// <c>CN</c> parameter was written using <see cref="CalDavIcs" />'s TEXT-property
/// backslash-escaping (RFC 5545 §3.3.11) — but a parameter value has its own, different
/// grammar (§3.2) with no backslash-escaping at all; a value needing COMMA/SEMICOLON/COLON must
/// be wrapped in a quoted-string instead. The old code's reader silently undid the same mistake
/// on the way back in, so MyloMail's own round trip "worked" by coincidence — but any other
/// calendar client, or a real server's correctly-quoted CN, saw or produced literal backslashes.
/// </summary>
public sealed class CalDavIcsAttendeeNameTests
{
	[Fact]
	public void An_organizer_name_with_a_comma_round_trips_through_the_reader_unmangled()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "event-1",
			ICalUid = "uid-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Organizer = new Address("Doe, Jane", "jane@example.test"),
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);
		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal("Doe, Jane", parsed.Organizer?.Name);
	}

	[Fact]
	public void An_attendee_name_with_a_semicolon_round_trips_through_the_reader_unmangled()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "event-1",
			ICalUid = "uid-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Attendees = [new Attendee("Doe; Jr, John", "john@example.test", AttendeeRole.Required, ResponseStatus.NeedsAction)],
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);
		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal("Doe; Jr, John", Assert.Single(parsed.Attendees).Name);
	}

	[Fact]
	public void A_bare_carriage_return_in_text_cannot_create_an_iCalendar_line()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "event-1",
			ICalUid = "uid-1",
			Title = "Standup\rX-Injected: yes",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);

		Assert.DoesNotContain("\rX-Injected", ics);
		Assert.Contains("SUMMARY:Standup\\nX-Injected: yes", ics);
	}

	[Fact]
	public void A_name_needing_no_quoting_is_written_bare_not_backslash_escaped()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "event-1",
			ICalUid = "uid-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Organizer = new Address("Jane Doe", "jane@example.test"),
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);

		Assert.Contains("ORGANIZER;CN=Jane Doe:mailto:jane@example.test", ics);
	}
}
