using System.Security.Cryptography;
using Ical.Net.DataTypes;
using MyloMail.Api.Domain;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace MyloMail.Api.Sync;

/// <summary>
/// One generated occurrence of a recurring master, before any override/cancellation has been
/// applied against it (§1, §13 Epic 7).
/// </summary>
public readonly record struct RecurrenceOccurrence(DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Expands a recurring master's <c>RRULE</c>/<c>RDATE</c>/<c>EXDATE</c> into concrete
/// occurrences within a window — query-time expansion (§13 Epic 7), since nothing before this
/// materialised anything beyond the master row's own single <see cref="CalendarEvent.Start"/>,
/// meaning a recurring event only ever appeared on the week it was created.
/// </summary>
/// <remarks>
/// Deliberately stateless and side-effect-free: this never writes to the database. Whether a
/// given occurrence is actually shown, replaced by an override, or suppressed by a
/// cancellation is the caller's job (see <c>MailHub.GetCalendarEvents</c>), because that
/// decision needs the master's sibling override rows, which this has no reason to know about.
/// </remarks>
public static class CalendarRecurrenceExpander
{
	/// <summary>
	/// A defensive backstop, not the normal bound — the caller's own <c>from</c>/<c>to</c>
	/// window is what should stop expansion in the overwhelming majority of cases. This exists
	/// only for a pathological rule (daily, no <c>COUNT</c>/<c>UNTIL</c>) paired with a
	/// pathologically wide requested window, so a single malformed event can never turn a
	/// calendar-view fetch into an unbounded loop.
	/// </summary>
	internal const int MaxOccurrences = 2000;

	/// <summary>
	/// Only the first <see cref="CalendarEvent.RecurrenceRules"/> entry is expanded. A VEVENT
	/// with more than one <c>RRULE</c> line is valid per RFC 5545 but vanishingly rare in
	/// practice — Ical.Net's own non-obsolete API dropped multi-rule support for the same
	/// reason (<c>RecurringComponent.RecurrenceRules</c> is marked obsolete in favour of the
	/// singular <c>RecurrenceRule</c>).
	/// </summary>
	public static IReadOnlyList<RecurrenceOccurrence> Expand(
		CalendarEvent master,
		DateTimeOffset from,
		DateTimeOffset to
	)
	{
		if (master.RecurrenceRules.Count == 0 || master.Start >= to)
		{
			return [];
		}

		var ev = new IcalEvent
		{
			Start = ToCalDateTime(master.Start, master.StartTimeZoneId),
			End = ToCalDateTime(master.End, master.EndTimeZoneId ?? master.StartTimeZoneId),
			RecurrenceRule = new RecurrencePattern(master.RecurrenceRules[0]),
		};

		foreach (var rdate in master.RecurrenceDates)
		{
			ev.RecurrenceDates.Add(ToCalDateTime(rdate, master.StartTimeZoneId));
		}
		foreach (var exdate in master.ExceptionDates)
		{
			ev.ExceptionDates.Add(ToCalDateTime(exdate, master.StartTimeZoneId));
		}

		var results = new List<RecurrenceOccurrence>();
		// Ical.Net's GetOccurrences is lazily evaluated and ordered ascending by start — an
		// unbounded rule (no COUNT/UNTIL) does not eagerly materialise every future
		// occurrence, so stopping the enumeration at `to` (or the defensive cap) is what
		// actually bounds the work, not something GetOccurrences does on its own.
		foreach (var occurrence in ev.GetOccurrences(ToCalDateTime(from, master.StartTimeZoneId)))
		{
			var occStart = ToDateTimeOffset(occurrence.Period.StartTime);
			if (occStart >= to)
			{
				break;
			}

			// EndTime is null when Ical.Net expresses the occurrence as a duration rather
			// than an explicit end — that never happens here, since Start/End are always
			// set explicitly above, but the duration is carried forward defensively anyway.
			var occEnd =
				occurrence.Period.EndTime is { } end
					? ToDateTimeOffset(end)
					: occStart + (master.End - master.Start);
			if (occEnd > from)
			{
				results.Add(new RecurrenceOccurrence(occStart, occEnd));
			}

			if (results.Count >= MaxOccurrences)
			{
				break;
			}
		}
		return results;
	}

	/// <summary>
	/// A stable, deterministic id for a virtual (not-yet-materialised) occurrence — the same
	/// master/instant always produces the same id, so the renderer's React keys stay stable
	/// across re-fetches without this ever touching the database.
	/// </summary>
	public static Guid VirtualOccurrenceId(Guid masterId, DateTimeOffset occurrenceStart)
	{
		Span<byte> input = stackalloc byte[24];
		masterId.TryWriteBytes(input);
		System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(input[16..], occurrenceStart.UtcTicks);
		Span<byte> hash = stackalloc byte[16];
		MD5.HashData(input, hash);
		// RFC 4122 §4.3 version-3-style bit twiddling: marks this as a name-based (hashed),
		// not random, GUID — purely cosmetic here, but keeps it visually distinct from a
		// real row's random Guid.NewGuid() id if anyone inspects one in a debugger.
		hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
		hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
		return new Guid(hash);
	}

	private static CalDateTime ToCalDateTime(DateTimeOffset value, string? timeZoneId) =>
		timeZoneId is null ? new CalDateTime(value.UtcDateTime) : FromInstant(value, timeZoneId);

	/// <summary>
	/// Ical.Net's <see cref="CalDateTime"/> constructor taking a time zone id expects a
	/// zone-local wall-clock value, not a UTC instant — so a stored UTC <see
	/// cref="DateTimeOffset"/> has to be converted into that zone's local time first, the same
	/// way <see cref="Providers.CalDav.CalDavIcs"/> already treats <c>StartTimeZoneId</c> as
	/// the authority for how <c>Start</c> is meant to be displayed/recur.
	/// </summary>
	private static CalDateTime FromInstant(DateTimeOffset instant, string timeZoneId)
	{
		var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
		var local = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
		return new CalDateTime(local, timeZoneId);
	}

	private static DateTimeOffset ToDateTimeOffset(CalDateTime value) => new(value.AsUtc, TimeSpan.Zero);
}
