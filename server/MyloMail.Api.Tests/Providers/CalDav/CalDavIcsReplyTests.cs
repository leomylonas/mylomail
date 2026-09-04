using MyloMail.Api.Domain;
using MyloMail.Api.Providers.CalDav;
using Xunit;

namespace MyloMail.Api.Tests.Providers.CalDav;

/// <summary>
/// Hundred-and-twenty-fourth architecture-review pass: <see cref="CalDavIcs.ToReplyIcs"/> never
/// emitted <c>RECURRENCE-ID</c> even when replying to a single overridden occurrence of a
/// recurring series — per RFC 5546 §3.2.3, a REPLY with no <c>RECURRENCE-ID</c> is ambiguous
/// with a reply to the whole series, and an organiser's client has no way to know the response
/// should be scoped to just one occurrence.
/// </summary>
public sealed class CalDavIcsReplyTests
{
	[Fact]
	public void A_reply_to_an_occurrence_override_carries_its_RECURRENCE_ID()
	{
		var occurrenceStart = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
		var ev = new CalendarEvent
		{
			ICalUid = "series-1",
			Title = "Standup",
			Start = occurrenceStart,
			End = occurrenceStart.AddMinutes(30),
			Organizer = new Address("Boss", "boss@example.test"),
			RecurrenceMasterId = Guid.NewGuid(),
			RecurrenceId = occurrenceStart,
		};

		var ics = CalDavIcs.ToReplyIcs(ev, new Address("Me", "me@example.test"), ResponseStatus.Accepted);

		Assert.Contains("RECURRENCE-ID:20260310T090000Z", ics);
	}

	[Fact]
	public void A_reply_to_the_series_master_carries_no_RECURRENCE_ID()
	{
		var ev = new CalendarEvent
		{
			ICalUid = "series-1",
			Title = "Standup",
			Start = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero),
			Organizer = new Address("Boss", "boss@example.test"),
			RecurrenceRules = ["FREQ=WEEKLY"],
		};

		var ics = CalDavIcs.ToReplyIcs(ev, new Address("Me", "me@example.test"), ResponseStatus.Accepted);

		Assert.DoesNotContain("RECURRENCE-ID", ics);
	}
}
