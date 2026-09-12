using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Sync;

/// <summary>
/// Builds the event list one calendar view shows for a window — non-recurring events as-is,
/// already-materialised override/cancelled instances as-is, and a recurring master's own
/// occurrences expanded query-time via <see cref="CalendarRecurrenceExpander"/> (§13 Epic 7).
/// </summary>
public static class CalendarEventOccurrences
{
	private const int MaximumSummariesPerCalendarView = 10_000;
	private const int MaximumSourceEventsPerCalendarView = 20_000;
	public static async Task<IReadOnlyList<CalendarEventSummaryDto>> ForCalendarAsync(
		MyloMailDbContext context,
		Guid calendarId,
		DateTimeOffset from,
		DateTimeOffset to,
		CancellationToken ct = default
	)
	{
		var events = await context.CalendarEvents
			.Where(e => e.CalendarId == calendarId)
			.Take(MaximumSourceEventsPerCalendarView)
			.ToListAsync(ct);

		var masters = events.Where(e => e.RecurrenceMasterId is null && HasRecurrenceSet(e)).ToList();
		var overrides = events.Where(e => e.RecurrenceMasterId is not null).ToList();
		var plain = events.Where(e =>
			e.RecurrenceMasterId is null && !HasRecurrenceSet(e) && e.Start < to && e.End > from
		);

		// RecurrenceId is always set on a real override/cancelled row (it's what
		// distinguishes one from a master) — filtered defensively rather than trusted, since
		// nothing in the schema itself enforces it.
		var overridesByMaster = overrides
			.Where(o => o.RecurrenceId is not null)
			.GroupBy(o => o.RecurrenceMasterId!.Value)
			.ToDictionary(g => g.Key, g => g.ToDictionary(o => o.RecurrenceId!.Value, o => o));

		var result = new List<CalendarEventSummaryDto>();
		foreach (var ev in plain)
		{
			if (result.Count >= MaximumSummariesPerCalendarView)
			{
				return [.. result.OrderBy(e => e.Start)];
			}
			result.Add(ToSummaryDto(ev, isVirtual: false, masterEventId: null));
		}

		// A real override/cancelled row is shown wherever it currently sits, independent of
		// where the occurrence it replaces originally was — the same "detect, don't silently
		// merge" spirit as §15's conflict handling: what actually happened wins over what was
		// originally scheduled.
		foreach (var ev in overrides)
		{
			if (ev.Start < to && ev.End > from)
			{
				if (result.Count >= MaximumSummariesPerCalendarView)
				{
					return [.. result.OrderBy(e => e.Start)];
				}
				result.Add(ToSummaryDto(ev, isVirtual: false, masterEventId: null));
			}
		}

		foreach (var master in masters)
		{
			overridesByMaster.TryGetValue(master.Id, out var overridesForMaster);

			// A non-standard StartTimeZoneId (Outlook/Exchange are known to emit vendor ids
			// like "Customized Time Zone" that aren't in the IANA/Windows tz database this
			// runs against) makes Expand's TimeZoneInfo.FindSystemTimeZoneById throw, and a
			// malformed RRULE string makes Ical.Net's RecurrencePattern constructor throw. One
			// bad master's provider-supplied data must not take down the whole calendar view
			// with it — every other master and every plain event in this window is unrelated
			// and still deserves to render.
			IReadOnlyList<RecurrenceOccurrence> occurrences;
			try
			{
				occurrences = CalendarRecurrenceExpander.Expand(master, from, to);
			}
			catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or FormatException or ArgumentException)
			{
				continue;
			}

			foreach (var occurrence in occurrences)
			{
				// The override map is keyed by the occurrence's *original* slot
				// (RECURRENCE-ID) — a cancelled or moved occurrence must not also show a
				// generated "ghost" at the time it used to be, even when the override
				// itself now sits outside [from, to) or isn't shown for some other reason.
				if (
					overridesForMaster is not null
					&& overridesForMaster.TryGetValue(occurrence.Start, out _)
				)
				{
					continue;
				}

				if (result.Count >= MaximumSummariesPerCalendarView)
				{
					return [.. result.OrderBy(e => e.Start)];
				}
				result.Add(
					new CalendarEventSummaryDto(
						CalendarRecurrenceExpander.VirtualOccurrenceId(master.Id, occurrence.Start),
						master.CalendarId,
						master.Title,
						master.Location,
						master.Description,
						occurrence.Start,
						occurrence.End,
						master.IsAllDay,
						master.Status,
						IsRecurring: true,
						SyncConflict: false,
						IsVirtualOccurrence: true,
						MasterEventId: master.Id,
						IsRecurrenceMaster: true
					)
				);
			}
		}

		return [.. result.OrderBy(e => e.Start)];
	}

	private static CalendarEventSummaryDto ToSummaryDto(CalendarEvent ev, bool isVirtual, Guid? masterEventId) =>
		new(
			ev.Id,
			ev.CalendarId,
			ev.Title,
			ev.Location,
			ev.Description,
			ev.Start,
			ev.End,
			ev.IsAllDay,
			ev.Status,
			HasRecurrenceSet(ev) || ev.RecurrenceMasterId != null,
			ev.SyncConflict,
			isVirtual,
			masterEventId,
			IsRecurrenceMaster: HasRecurrenceSet(ev)
		);

	private static bool HasRecurrenceSet(CalendarEvent ev) =>
		ev.RecurrenceRules.Count > 0
		|| ev.RecurrenceDates.Count > 0
		|| ev.ExceptionDates.Count > 0;
}
