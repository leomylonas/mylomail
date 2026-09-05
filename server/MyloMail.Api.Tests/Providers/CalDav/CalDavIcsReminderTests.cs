using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-ninety-first architecture-review pass: <c>ParseEvents</c> has always read a
/// VEVENT's <c>VALARM</c> triggers into <see cref="CalendarEventDto.Reminders"/> (surfaced
/// read-only in <c>EventModal.tsx</c>), but <c>RenderVEvent</c> never wrote a <c>VALARM</c> block
/// back — so any edit made through this app (title, time, location, anything) silently stripped
/// every reminder from the resource on the next PUT, even though nothing about the edit touched
/// reminders at all.
/// </summary>
public sealed class CalDavIcsReminderTests
{
	[Fact]
	public void A_reminder_is_written_as_a_VALARM_block()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "event-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Reminders = [new DateTimeOffset(2026, 3, 10, 8, 45, 0, TimeSpan.Zero)],
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);

		Assert.Contains("BEGIN:VALARM", ics);
		Assert.Contains("TRIGGER:-PT15M", ics);
		Assert.Contains("ACTION:DISPLAY", ics);
	}

	/// <summary>
	/// The actual bug: editing an event that already has a reminder must not lose it. Confirmed
	/// as a genuine discriminator — reverting the <c>RenderVEvent</c> fix makes this fail with
	/// zero reminders surviving the round trip.
	/// </summary>
	[Fact]
	public void A_reminder_survives_a_render_then_parse_round_trip()
	{
		var reminderInstant = new DateTimeOffset(2026, 3, 10, 8, 45, 0, TimeSpan.Zero);
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "event-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Reminders = [reminderInstant],
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);
		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		var roundTripped = Assert.Single(parsed.Reminders);
		Assert.Equal(reminderInstant, roundTripped);
	}

	/// <summary>
	/// The follow-up gap `invariant-review` found in this same pass: <c>ParseEvents</c> collected
	/// every line between <c>BEGIN:VEVENT</c>/<c>END:VEVENT</c> into one flat list with no
	/// awareness of a nested <c>BEGIN:VALARM</c>/<c>END:VALARM</c> block, so the VALARM's own
	/// <c>DESCRIPTION</c> (the reminder text) silently overwrote the VEVENT's real
	/// <c>DESCRIPTION</c> on a round trip, since both share the same property name and
	/// <c>ToDto</c>'s <c>Single</c> lookup takes the last match. Confirmed as a genuine
	/// discriminator — reverting the VALARM-nesting fix in <c>ParseEvents</c> makes this fail with
	/// the description replaced by the event's own title.
	/// </summary>
	[Fact]
	public void An_events_own_description_survives_a_round_trip_alongside_a_reminder()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "event-1",
			Title = "Standup",
			Description = "Daily sync with the team",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Reminders = [new DateTimeOffset(2026, 3, 10, 8, 45, 0, TimeSpan.Zero)],
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);
		var parsed = Assert.Single(CalDavIcs.ParseEvents(ics, "href", "etag"));

		Assert.Equal("Daily sync with the team", parsed.Description);
		Assert.Single(parsed.Reminders);
	}

	[Fact]
	public void An_event_with_no_reminders_writes_no_VALARM_block()
	{
		var ev = new CalendarEventDto
		{
			ProviderEventId = "evt-1",
			ICalUid = "event-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
		};

		var ics = CalDavIcs.ToIcs("event-1", ev);

		Assert.DoesNotContain("VALARM", ics);
	}
}
