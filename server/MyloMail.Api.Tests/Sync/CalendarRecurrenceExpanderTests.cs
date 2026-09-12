using MyloMail.Api.Domain;
using MyloMail.Api.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// User-approved feature, following a forty-eighth-pass finding: recurring calendar events
/// never actually recurred in the UI — a weekly event showed once, on the week it was
/// created, and never again. §13 Epic 7 requires "correct time/timezone/recurrence handling."
/// </summary>
public sealed class CalendarRecurrenceExpanderTests
{
	[Fact]
	public void A_weekly_rule_expands_within_the_requested_window()
	{
		var monday = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
		var master = Master(monday, monday.AddHours(1), "FREQ=WEEKLY;COUNT=6");

		var occurrences = CalendarRecurrenceExpander.Expand(master, monday.AddDays(7), monday.AddDays(21));

		Assert.Equal(
			[monday.AddDays(7), monday.AddDays(14)],
			occurrences.Select(o => o.Start)
		);
	}

	/// <summary>A window ending exactly at the series' own COUNT/UNTIL boundary includes
	/// nothing past it — the expansion must stop, not run past what the rule allows.</summary>
	[Fact]
	public void A_count_bound_series_does_not_expand_past_its_own_end()
	{
		var monday = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
		var master = Master(monday, monday.AddHours(1), "FREQ=WEEKLY;COUNT=3");

		// A window that would otherwise fit 10 weekly occurrences.
		var occurrences = CalendarRecurrenceExpander.Expand(master, monday, monday.AddDays(70));

		Assert.Equal(3, occurrences.Count);
	}

	[Fact]
	public void An_exception_date_is_excluded()
	{
		var monday = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
		var master = Master(monday, monday.AddHours(1), "FREQ=WEEKLY;COUNT=4");
		master.ExceptionDates = [monday.AddDays(7)];

		var occurrences = CalendarRecurrenceExpander.Expand(master, monday, monday.AddDays(28));

		Assert.DoesNotContain(monday.AddDays(7), occurrences.Select(o => o.Start));
		Assert.Equal(3, occurrences.Count);
	}

	/// <summary>
	/// A weekly 9am America/New_York event straddling the 2026 US "spring forward" DST
	/// transition (2026-03-08) must keep 9am local wall-clock time on both sides, which means
	/// its UTC instant shifts by exactly one hour across the boundary.
	/// </summary>
	[Fact]
	public void Recurrence_stays_correct_across_a_dst_transition()
	{
		// 9am America/New_York on 2026-03-01 is 14:00 UTC (still EST, UTC-5).
		var firstUtc = new DateTimeOffset(2026, 3, 1, 14, 0, 0, TimeSpan.Zero);
		var master = Master(firstUtc, firstUtc.AddHours(1), "FREQ=WEEKLY;COUNT=3");
		master.StartTimeZoneId = "America/New_York";

		var occurrences = CalendarRecurrenceExpander.Expand(master, firstUtc, firstUtc.AddDays(21));

		Assert.Equal(3, occurrences.Count);
		Assert.Equal(firstUtc, occurrences[0].Start);
		// 2026-03-08 is after the transition (EDT, UTC-4): 9am local is 13:00 UTC, not 14:00.
		Assert.Equal(new DateTimeOffset(2026, 3, 8, 13, 0, 0, TimeSpan.Zero), occurrences[1].Start);
		Assert.Equal(new DateTimeOffset(2026, 3, 15, 13, 0, 0, TimeSpan.Zero), occurrences[2].Start);
	}

	/// <summary>A pathological unbounded rule against a huge window must still terminate,
	/// bounded by the defensive cap rather than the caller's own window.</summary>
	[Fact]
	public void An_unbounded_rule_is_capped_defensively()
	{
		var start = new DateTimeOffset(2000, 1, 1, 9, 0, 0, TimeSpan.Zero);
		var master = Master(start, start.AddMinutes(30), "FREQ=DAILY");

		var occurrences = CalendarRecurrenceExpander.Expand(master, start, start.AddYears(200));

		Assert.True(occurrences.Count <= MaxOccurrencesConstant());
	}

	private static int MaxOccurrencesConstant() =>
		(int)typeof(CalendarRecurrenceExpander)
			.GetField("MaxOccurrences", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
			.GetValue(null)!;

	[Fact]
	public void The_same_master_and_instant_always_produce_the_same_virtual_id()
	{
		var masterId = Guid.NewGuid();
		var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

		var first = CalendarRecurrenceExpander.VirtualOccurrenceId(masterId, start);
		var second = CalendarRecurrenceExpander.VirtualOccurrenceId(masterId, start);

		Assert.Equal(first, second);
		Assert.NotEqual(first, CalendarRecurrenceExpander.VirtualOccurrenceId(Guid.NewGuid(), start));
	}

	[Fact]
	public void An_RDATE_only_set_expands_the_added_occurrence()
	{
		var start = new DateTimeOffset(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
		var additional = start.AddDays(2);
		var master = Master(start, start.AddMinutes(30), "FREQ=DAILY");
		master.RecurrenceRules = [];
		master.RecurrenceDates = [additional];

		var occurrences = CalendarRecurrenceExpander.Expand(
			master,
			start,
			additional.AddDays(1)
		);

		Assert.Contains(occurrences, occurrence => occurrence.Start == additional);
	}

	private static CalendarEvent Master(DateTimeOffset start, DateTimeOffset end, string rrule) =>
		new()
		{
			Id = Guid.NewGuid(),
			Title = "Recurring event",
			Start = start,
			End = end,
			RecurrenceRules = [rrule],
		};
}
