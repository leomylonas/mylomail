using System.Globalization;
using System.Text;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>
/// iCalendar (RFC 5545) VEVENT parsing and generation — the wire format CalDAV carries
/// natively, unlike Graph's translated <c>recurrence</c> object (§1).
/// </summary>
/// <remarks>
/// One .ics resource commonly holds a recurrence master plus its overridden instances as
/// separate <c>VEVENT</c> components sharing one <c>UID</c>. Each becomes its own
/// <see cref="CalendarEventDto"/>; the master's provider id is the resource href, an
/// override's is the href plus its <c>RECURRENCE-ID</c>, because the sync page upserts by
/// provider event id and the two are not interchangeable.
/// </remarks>
internal static partial class CalDavIcs
{
	private const int MaximumUnfoldedLines = 65_536;
	private const int MaximumPropertiesPerEvent = 4_096;
	private const int MaximumTextFieldLength = 4_096;
	private const int MaximumAttendeesPerEvent = 512;
	private const int MaximumRecurrenceValuesPerEvent = 512;
	private const int MaximumPropertyHeadLength = 8_192;
	private const int MaximumParametersPerProperty = 64;
	public static IReadOnlyList<CalendarEventDto> ParseEvents(string ics, string href, string etag, int maxEvents = 512)
	{
		var lines = Unfold(ics);
		var events = new List<CalendarEventDto>();
		List<(string Name, IReadOnlyDictionary<string, string> Params, string Value)>? current = null;
		// A VEVENT can nest its own components — VALARM being the only one this codebase reads or
		// writes today — whose properties can share names with VEVENT-level ones (a reminder's
		// own DESCRIPTION vs. the event's DESCRIPTION). Only VALARM's TRIGGER is something this
		// parser actually wants (into Reminders, via All("TRIGGER")); every other nested property
		// must not land in this VEVENT's own flat property list, or it silently overwrites the
		// real one whenever ToDto's Single(name) takes the last match.
		var insideValarm = false;

		foreach (var line in lines)
		{
			if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
			{
				current = [];
				insideValarm = false;
				continue;
			}
			if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
			{
				if (current is not null)
				{
					if (events.Count >= maxEvents)
					{
						throw new InvalidDataException($"Calendar resource exceeds the {maxEvents}-event limit.");
					}
					events.Add(ToDto(current, href, etag));
				}
				current = null;
				continue;
			}
			if (current is null)
			{
				continue;
			}
			if (line.Equals("BEGIN:VALARM", StringComparison.OrdinalIgnoreCase))
			{
				insideValarm = true;
				continue;
			}
			if (line.Equals("END:VALARM", StringComparison.OrdinalIgnoreCase))
			{
				insideValarm = false;
				continue;
			}
			if (!insideValarm)
			{
				if (current.Count >= MaximumPropertiesPerEvent)
				{
					throw new InvalidDataException($"Calendar event exceeds the {MaximumPropertiesPerEvent}-property limit.");
				}
				current.Add(ParseLine(line));
				continue;
			}
			var parsed = ParseLine(line);
			if (parsed.Name.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase))
			{
				if (current.Count >= MaximumPropertiesPerEvent)
				{
					throw new InvalidDataException($"Calendar event exceeds the {MaximumPropertiesPerEvent}-property limit.");
				}
				current.Add(parsed);
			}
		}
		if (current is not null)
		{
			throw new InvalidDataException("Calendar data contains an unterminated VEVENT.");
		}

		return events;
	}
	/// <summary>
	/// The iTIP <c>METHOD</c> from a full <c>VCALENDAR</c> document's own top-level property —
	/// <c>REQUEST</c>, <c>CANCEL</c>, <c>REPLY</c>, etc. (RFC 5546) — distinguishing an invite
	/// from a cancellation or someone else's reply landing as mail (§13 Epic 7). Not present on
	/// a CalDAV resource, which is why <see cref="ParseEvents"/> never needed this: that always
	/// reads a stored event, never a message someone sent.
	/// </summary>
	public static string? ParseMethod(string ics)
	{
		foreach (var line in Unfold(ics))
		{
			if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
			{
				// METHOD is a VCALENDAR-level property; once VEVENT starts it cannot appear.
				return null;
			}
			var (name, _, value) = ParseLine(line);
			if (name.Equals("METHOD", StringComparison.OrdinalIgnoreCase))
			{
				return value;
			}
		}
		return null;
	}

	public static string ToIcs(string uid, CalendarEventDto ev) =>
		$"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//MyloMail//CalDAV//EN\r\n{RenderVEvent(uid, ev)}END:VCALENDAR\r\n";

	/// <summary>
	/// One <c>VEVENT</c>'s lines, <c>BEGIN:VEVENT</c> through <c>END:VEVENT</c> inclusive — a
	/// resource commonly holds several sharing one <c>UID</c> (a recurrence master plus its
	/// overrides), so this is the unit both a whole-resource PUT (<see cref="ToIcs"/>) and a
	/// single-override merge (<see cref="MergeOverride"/>) actually build.
	/// </summary>
	private static string RenderVEvent(string uid, CalendarEventDto ev)
	{
		var lines = new List<string> { "BEGIN:VEVENT" };
		lines.Add($"UID:{Escape(uid)}");
		lines.Add($"DTSTAMP:{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}");
		lines.Add(FormatDateTimeProperty("DTSTART", ev.Start, ev.IsAllDay, ev.StartTimeZoneId));
		lines.Add(FormatDateTimeProperty("DTEND", ev.End, ev.IsAllDay, ev.EndTimeZoneId));
		lines.Add($"SUMMARY:{Escape(ev.Title)}");
		if (ev.Location is { Length: > 0 })
		{
			lines.Add($"LOCATION:{Escape(ev.Location)}");
		}
		if (ev.Description is { Length: > 0 })
		{
			lines.Add($"DESCRIPTION:{Escape(ev.Description)}");
		}
		lines.Add($"SEQUENCE:{ev.Sequence}");
		lines.Add($"STATUS:{ev.Status switch
		{
			EventStatus.Tentative => "TENTATIVE",
			EventStatus.Cancelled => "CANCELLED",
			_ => "CONFIRMED",
		}}");
		if (ev.Organizer is { } organizer)
		{
			var cn = organizer.Name is { Length: > 0 } ? $";CN={QuoteParamValue(organizer.Name)}" : "";
			lines.Add($"ORGANIZER{cn}:mailto:{organizer.Email}");
		}
		foreach (var attendee in ev.Attendees)
		{
			var cn = attendee.Name is { Length: > 0 } ? $";CN={QuoteParamValue(attendee.Name)}" : "";
			var role = attendee.Role switch
			{
				AttendeeRole.Optional => "OPT-PARTICIPANT",
				AttendeeRole.Resource => "NON-PARTICIPANT",
				_ => "REQ-PARTICIPANT",
			};
			var partstat = attendee.ResponseStatus switch
			{
				ResponseStatus.Accepted => "ACCEPTED",
				ResponseStatus.Declined => "DECLINED",
				ResponseStatus.Tentative => "TENTATIVE",
				_ => "NEEDS-ACTION",
			};
			lines.Add($"ATTENDEE{cn};ROLE={role};PARTSTAT={partstat}:mailto:{attendee.Email}");
		}
		foreach (var rule in ev.RecurrenceRules)
		{
			lines.Add($"RRULE:{rule}");
		}
		if (ev.RecurrenceDates.Count > 0)
		{
			lines.Add(
				$"{RecurrenceSetPropertyName("RDATE", ev.IsAllDay, ev.StartTimeZoneId)}:{string.Join(',', ev.RecurrenceDates.Select(d => FormatDateTimeValue(d, ev.IsAllDay, ev.StartTimeZoneId)))}"
			);
		}
		if (ev.ExceptionDates.Count > 0)
		{
			lines.Add(
				$"{RecurrenceSetPropertyName("EXDATE", ev.IsAllDay, ev.StartTimeZoneId)}:{string.Join(',', ev.ExceptionDates.Select(d => FormatDateTimeValue(d, ev.IsAllDay, ev.StartTimeZoneId)))}"
			);
		}
		if (ev.RecurrenceId is { } recurrenceId)
		{
			lines.Add(FormatDateTimeProperty("RECURRENCE-ID", recurrenceId, ev.IsAllDay, ev.StartTimeZoneId));
		}
		foreach (var reminder in ev.Reminders)
		{
			// Written as a DURATION-relative TRIGGER (RFC 5545 §3.8.6.3), not the DATE-TIME form:
			// ToDto/ParseEvents itself only ever produces a reminder as start+duration (there is
			// no per-reminder flag recording "this one came in as an absolute DATE-TIME"), so a
			// relative TRIGGER is the only form this round-trips through faithfully.
			lines.Add("BEGIN:VALARM");
			lines.Add("ACTION:DISPLAY");
			lines.Add($"DESCRIPTION:{Escape(ev.Title)}");
			lines.Add($"TRIGGER:{FormatDuration(reminder - ev.Start)}");
			lines.Add("END:VALARM");
		}
		lines.Add("END:VEVENT");
		return string.Join("\r\n", lines) + "\r\n";
	}

	/// <summary>
	/// Replaces one override's <c>VEVENT</c> within an existing resource's raw text, leaving
	/// the master and every other override byte-for-byte as the server sent them — appends
	/// before <c>END:VCALENDAR</c> if no existing block has this <c>RECURRENCE-ID</c> (a new
	/// override, not yet on the server).
	/// </summary>
	/// <remarks>
	/// This is what makes editing a single recurrence-override instance safe: the whole
	/// resource is one PUT, so an update built the way <see cref="ToIcs"/> builds a fresh
	/// resource would silently discard the master and every other override sharing it.
	/// Untouched blocks are copied verbatim rather than round-tripped through
	/// <see cref="ParseEvents"/>/<see cref="RenderVEvent"/> specifically to avoid losing any
	/// property this provider does not itself model (custom `X-` properties, `VALARM`, and so
	/// on) on an instance nobody asked to change.
	/// </remarks>
	public static string MergeOverride(string ics, string uid, CalendarEventDto overrideEvent)
	{
		var newBlock = RenderVEvent(uid, overrideEvent);
		var targetRecurrenceId = overrideEvent.RecurrenceId;
		var result = new StringBuilder(ics.Length + newBlock.Length);
		var cursor = 0;
		var blocks = 0;
		var replaced = false;
		while (true)
		{
			var start = FindLineMarker(ics, "BEGIN:VEVENT", cursor);
			if (start < 0)
			{
				result.Append(ics, cursor, ics.Length - cursor);
				break;
			}
			var endMarker = FindLineMarker(ics, "END:VEVENT", start);
			if (endMarker < 0 || ++blocks > 512)
			{
				throw new InvalidOperationException("CalDAV resource has an invalid or oversized VEVENT structure.");
			}
			var end = endMarker + "END:VEVENT".Length;
			if (end < ics.Length && ics[end] == '\r')
			{
				end++;
			}
			if (end < ics.Length && ics[end] == '\n')
			{
				end++;
			}

			result.Append(ics, cursor, start - cursor);
			var block = ics[start..end];
			if (!replaced && BlockRecurrenceId(block) == targetRecurrenceId)
			{
				result.Append(newBlock);
				replaced = true;
			}
			else
			{
				result.Append(block);
			}
			cursor = end;
		}

		if (replaced)
		{
			return result.ToString();
		}

		var merged = result.ToString();
		var closing = merged.LastIndexOf("END:VCALENDAR", StringComparison.OrdinalIgnoreCase);
		return closing < 0 ? merged + newBlock : merged[..closing] + newBlock + merged[closing..];
	}

	private static int FindLineMarker(string text, string marker, int start)
	{
		var index = Math.Max(0, start);
		while (index < text.Length)
		{
			index = text.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase);
			if (index < 0)
			{
				return -1;
			}
			if ((index == 0 || text[index - 1] is '\n' or '\r')
				&& (index + marker.Length == text.Length
					|| text[index + marker.Length] is '\r' or '\n'))
			{
				return index;
			}
			index += marker.Length;
		}
		return -1;
	}

	private static DateTimeOffset? BlockRecurrenceId(string block)

	{
		foreach (var line in Unfold(block))
		{
			if (!line.StartsWith("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var (_, parameters, value) = ParseLine(line);
			return ParseDateTime(parameters, value);
		}
		return null;
	}


	/// <summary>
	/// An iTIP <c>REPLY</c> to an invite (§13 Epic 7): one <c>VEVENT</c> naming only the
	/// replying attendee, per RFC 5546 §3.2.3 — a reply describes the sender's own
	/// participation status, not the whole attendee list, which the organiser already has.
	/// </summary>
	public static string ToReplyIcs(CalendarEvent ev, Address replyingAs, ResponseStatus status)
	{
		var partstat = status switch
		{
			ResponseStatus.Accepted => "ACCEPTED",
			ResponseStatus.Declined => "DECLINED",
			ResponseStatus.Tentative => "TENTATIVE",
			_ => "NEEDS-ACTION",
		};
		var cn = replyingAs.Name is { Length: > 0 } ? $";CN={QuoteParamValue(replyingAs.Name)}" : "";
		var organizerCn = ev.Organizer?.Name is { Length: > 0 } ? $";CN={QuoteParamValue(ev.Organizer.Name)}" : "";

		var lines = new List<string>
		{
			"BEGIN:VCALENDAR",
			"VERSION:2.0",
			"PRODID:-//MyloMail//CalDAV//EN",
			"METHOD:REPLY",
			"BEGIN:VEVENT",
			$"UID:{Escape(ev.ICalUid)}",
			$"DTSTAMP:{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}",
			FormatDateTimeProperty("DTSTART", ev.Start, ev.IsAllDay, ev.StartTimeZoneId),
			FormatDateTimeProperty("DTEND", ev.End, ev.IsAllDay, ev.EndTimeZoneId),
			$"SUMMARY:{Escape(ev.Title)}",
			$"SEQUENCE:{ev.Sequence}",
		};
		if (ev.Organizer is { } organizer)
		{
			lines.Add($"ORGANIZER{organizerCn}:mailto:{organizer.Email}");
		}
		if (ev.RecurrenceId is { } recurrenceId)
		{
			// Without this, a REPLY to a single occurrence of a recurring series is
			// indistinguishable from a REPLY to the series master (RFC 5546 §3.2.3) — the
			// organiser's client has no way to know this response is scoped to one occurrence,
			// and could apply it to the whole series instead.
			lines.Add(FormatDateTimeProperty("RECURRENCE-ID", recurrenceId, ev.IsAllDay, ev.StartTimeZoneId));
		}
		lines.Add($"ATTENDEE{cn};PARTSTAT={partstat}:mailto:{replyingAs.Email}");
		lines.Add("END:VEVENT");
		lines.Add("END:VCALENDAR");
		return string.Join("\r\n", lines) + "\r\n";
	}

	private static CalendarEventDto ToDto(
		List<(string Name, IReadOnlyDictionary<string, string> Params, string Value)> props,
		string href,
		string etag
	)
	{
		string? Single(string name) => props.LastOrDefault(p => p.Name == name).Value;
		(IReadOnlyDictionary<string, string> Params, string Value)? SingleWithParams(string name)
		{
			var found = props.LastOrDefault(p => p.Name == name);
			return found.Name is null ? null : (found.Params, found.Value);
		}
		IEnumerable<(IReadOnlyDictionary<string, string> Params, string Value)> All(string name) =>
			props.Where(p => p.Name == name).Select(p => (p.Params, p.Value));

		var responseTimestamp = SingleWithParams("DTSTAMP") is { } dtstamp
			? ParseDateTime(dtstamp.Params, dtstamp.Value)
			: (DateTimeOffset?)null;

		var uid = Single("UID") ?? href;
		var isAllDay = ParamEquals(SingleWithParams("DTSTART")?.Params, "VALUE", "DATE");
		var start = SingleWithParams("DTSTART") is { } dtstart ? ParseDateTime(dtstart.Params, dtstart.Value) : DateTimeOffset.UnixEpoch;
		// RFC 5545 §3.6.1: DTEND and DURATION are mutually exclusive on a VEVENT — a server or
		// another client may legitimately emit DURATION instead of DTEND (common for all-day and
		// templated recurring events). Absent either, the spec's own default duration is one day
		// for a DATE-valued DTSTART, zero otherwise — never an arbitrary fixed hour.
		var end = SingleWithParams("DTEND") is { } dtend
			? ParseDateTime(dtend.Params, dtend.Value)
			: Single("DURATION") is { } duration && TryParseDuration(duration, out var vEventDuration)
				? SafeAdd(start, vEventDuration)
				: isAllDay
					? SafeAdd(start, TimeSpan.FromDays(1))
					: start;
		var recurrenceId = SingleWithParams("RECURRENCE-ID") is { } rid ? ParseDateTime(rid.Params, rid.Value) : (DateTimeOffset?)null;
		var providerEventId = recurrenceId is null ? href : $"{href}#{recurrenceId:O}";

		return new CalendarEventDto
		{
			ProviderEventId = providerEventId,
			ICalUid = uid,
			ProviderRevision = etag,
			Sequence = int.TryParse(Single("SEQUENCE"), out var seq) ? seq : 0,
			Title = LimitText(Unescape(Single("SUMMARY"))) ?? string.Empty,
			Location = LimitText(Unescape(Single("LOCATION"))),
			Description = LimitText(Unescape(Single("DESCRIPTION"))),
			Start = start,
			End = end,
			StartTimeZoneId = KnownTimeZoneId(SingleWithParams("DTSTART")?.Params.GetValueOrDefault("TZID")),
			EndTimeZoneId = KnownTimeZoneId(SingleWithParams("DTEND")?.Params.GetValueOrDefault("TZID")),
			IsAllDay = isAllDay,
			Organizer = SingleWithParams("ORGANIZER") is { } organizer
				? new Address(organizer.Params.GetValueOrDefault("CN"), StripMailto(organizer.Value))
				: null,
			Attendees = All("ATTENDEE")
				.Take(MaximumAttendeesPerEvent)
				.Select(a => new Attendee(
					LimitText(a.Params.GetValueOrDefault("CN")),
					LimitText(StripMailto(a.Value)) ?? string.Empty,
					a.Params.GetValueOrDefault("ROLE")?.ToUpperInvariant() switch
					{
						"OPT-PARTICIPANT" => AttendeeRole.Optional,
						"NON-PARTICIPANT" => AttendeeRole.Resource,
						_ => AttendeeRole.Required,
					},
					a.Params.GetValueOrDefault("PARTSTAT")?.ToUpperInvariant() switch
					{
						"ACCEPTED" => ResponseStatus.Accepted,
						"DECLINED" => ResponseStatus.Declined,
						"TENTATIVE" => ResponseStatus.Tentative,
						_ => ResponseStatus.NeedsAction,
					},
					responseTimestamp
				))
				.ToList(),
			Status = Single("STATUS")?.ToUpperInvariant() switch
			{
				"TENTATIVE" => EventStatus.Tentative,
				"CANCELLED" => EventStatus.Cancelled,
				_ => EventStatus.Confirmed,
			},
			Reminders = All("TRIGGER")
				.Select(t => TryParseTrigger(t.Params, t.Value, start, end))
				.Where(r => r is not null)
				.Select(r => r!.Value)
				.ToList(),
			RecurrenceRules = BoundedRecurrenceRules(All("RRULE")),
			// Each occurrence's own Params, not NoParams: RFC 5545 requires an RDATE/EXDATE to
			// carry the same VALUE type and TZID as DTSTART (§3.8.5.1/§3.8.5.2).
			RecurrenceDates = ParseRecurrenceDates(All("RDATE")),
			ExceptionDates = ParseRecurrenceDates(All("EXDATE")),
			RecurrenceMasterProviderEventId = recurrenceId is null ? null : href,
			RecurrenceId = recurrenceId,
		};
	}

	private static List<DateTimeOffset> ParseRecurrenceDates(
		IEnumerable<(IReadOnlyDictionary<string, string> Params, string Value)> properties
	)
	{
		var dates = new List<DateTimeOffset>(MaximumRecurrenceValuesPerEvent);
		foreach (var property in properties)
		{
			var valueStart = 0;
			while (valueStart <= property.Value.Length)
			{
				if (dates.Count == MaximumRecurrenceValuesPerEvent)
				{
					throw new InvalidDataException($"Calendar recurrence set exceeds the {MaximumRecurrenceValuesPerEvent}-value limit.");
				}
				var separator = property.Value.IndexOf(',', valueStart);
				var valueEnd = separator < 0 ? property.Value.Length : separator;
				dates.Add(ParseDateTime(property.Params, property.Value[valueStart..valueEnd]));
				if (separator < 0)
				{
					break;
				}
				valueStart = separator + 1;
			}
		}
		return dates;
	}

	private static List<string> BoundedRecurrenceRules(
		IEnumerable<(IReadOnlyDictionary<string, string> Params, string Value)> properties
	)
	{
		var rules = new List<string>(MaximumRecurrenceValuesPerEvent);
		foreach (var property in properties)
		{
			if (rules.Count == MaximumRecurrenceValuesPerEvent)
			{
				throw new InvalidDataException($"Calendar recurrence set exceeds the {MaximumRecurrenceValuesPerEvent}-rule limit.");
			}
			rules.Add(LimitText(property.Value) ?? string.Empty);
		}
		return rules;
	}

	private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

	private static string? LimitText(string? value) =>
		value is { Length: > MaximumTextFieldLength } ? value[..MaximumTextFieldLength] : value;

	private static string StripMailto(string value) =>
		value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value["mailto:".Length..] : value;

	private static DateTimeOffset? TryParseTrigger(IReadOnlyDictionary<string, string> parameters, string value, DateTimeOffset start, DateTimeOffset end)
	{
		if (ParamEquals(parameters, "VALUE", "DATE-TIME"))
		{
			return ParseDateTime(parameters, value);
		}
		if (!TryParseDuration(value, out var duration))
		{
			return null;
		}
		// RFC 5545 §3.8.6.3: RELATED defaults to START, but a server may explicitly say a
		// duration-relative trigger is relative to the event's END instead (e.g. "15 minutes
		// before an all-day event's end") — anchoring to start unconditionally silently fires
		// the reminder at the wrong instant for any event whose end differs from its start.
		var anchor = ParamEquals(parameters, "RELATED", "END") ? end : start;
		try
		{
			return anchor + duration;
		}
		catch (ArgumentOutOfRangeException ex)
		{
			throw new InvalidDataException("Calendar reminder trigger is outside the supported date range.", ex);
		}
	}

	/// <see cref="ParseLine"/> only uppercases parameter *names*, never their values, so every
	/// comparison against one of these known tokens must be explicitly case-insensitive here
	/// rather than relying on the value already being normalized.
	/// </summary>
	private static bool ParamEquals(IReadOnlyDictionary<string, string>? parameters, string key, string expected) =>
		string.Equals(parameters?.GetValueOrDefault(key), expected, StringComparison.OrdinalIgnoreCase);

	/// <summary>The inverse of <see cref="TryParseDuration"/>, for a VALARM's own TRIGGER.</summary>
	private static string FormatDuration(TimeSpan span)
	{
		var negative = span < TimeSpan.Zero;
		var abs = negative ? -span : span;
		var days = abs.Days;
		var hours = abs.Hours;
		var minutes = abs.Minutes;
		var seconds = abs.Seconds;
		var time = hours == 0 && minutes == 0 && seconds == 0
			? ""
			: $"T{(hours > 0 ? $"{hours}H" : "")}{(minutes > 0 ? $"{minutes}M" : "")}{(seconds > 0 ? $"{seconds}S" : "")}";
		var date = days > 0 ? $"{days}D" : "";
		// RFC 5545 §3.3.6: a zero-length duration must still be well-formed (P0D is the shortest).
		return $"{(negative ? "-" : "")}P{date}{time}" is var text && text is "P" ? "P0D" : text;
	}

	/// <summary>Prevents a syntactically valid duration from overflowing a provider event end time.</summary>
	private static DateTimeOffset SafeAdd(DateTimeOffset start, TimeSpan duration)
	{
		try
		{
			return start + duration;
		}
		catch (ArgumentOutOfRangeException)
		{
			return start;
		}
	}

	/// <summary>A minimal ISO-8601 duration parser covering the subset VALARM triggers use.</summary>
	private static bool TryParseDuration(string value, out TimeSpan duration)
	{
		duration = TimeSpan.Zero;
		// RFC 5234 §2.3: the P/T/D/W/H/M/S designators in RFC 5545's dur-value grammar (§3.3.6)
		// are quoted ABNF literals, case-insensitive like the parameter-value tokens and UTC 'Z'
		// suffix already fixed elsewhere in this file — a compliant server may send "-pt15m" just
		// as validly as "-PT15M". Normalizing case up front means the digit/sign parsing below
		// (unaffected by case either way) never has to special-case it.
		var span = value.ToUpperInvariant().AsSpan();
		var negative = false;
		if (span.Length > 0 && (span[0] == '+' || span[0] == '-'))
		{
			negative = span[0] == '-';
			span = span[1..];
		}
		if (span.Length == 0 || span[0] != 'P')
		{
			return false;
		}
		span = span[1..];
		var inTime = false;
		try
		{
			var total = TimeSpan.Zero;
			var number = 0;
			var hasNumber = false;
			foreach (var c in span)
			{
				if (c == 'T')
				{
					inTime = true;
					continue;
				}
				if (char.IsDigit(c))
				{
					number = checked(number * 10 + (c - '0'));
					hasNumber = true;
					continue;
				}
				if (!hasNumber)
				{
					return false;
				}
				total = checked(total + c switch
				{
					'D' => TimeSpan.FromDays(number),
					'W' => TimeSpan.FromDays(checked(number * 7)),
					'H' => TimeSpan.FromHours(number),
					'M' => inTime ? TimeSpan.FromMinutes(number) : TimeSpan.Zero,
					'S' => TimeSpan.FromSeconds(number),
					_ => TimeSpan.Zero,
				});
				number = 0;
				hasNumber = false;
			}
			duration = negative ? -total : total;
			return true;
		}
		catch (OverflowException)
		{
			duration = default;
			return false;
		}
	}

	/// <summary>
	/// A TZID this runtime's own <see cref="TimeZoneInfo"/> database can't resolve is exactly
	/// the case <see cref="ParseDateTime"/> already falls back to treating as a floating/UTC
	/// instant rather than throwing — but storing the unresolvable id anyway meant a later
	/// <see cref="RenderVEvent"/> (any edit that re-saves this event, even one touching only its
	/// title) would write it straight back into a <c>TZID=</c> parameter while
	/// <see cref="FormatLocal"/> silently fell back to writing the raw UTC instant under it, since
	/// it hits the exact same unresolvable-zone case. The result declares a time zone the value
	/// was never actually converted into — a mismatch a compliant reader (this app included, on
	/// its own next parse) would then apply the wrong offset to. Returning null here instead
	/// keeps what was already parsed as the honest floating/UTC interpretation, consistent all
	/// the way through a write-back.
	/// </summary>
	private static string? KnownTimeZoneId(string? tzid)
	{
		if (string.IsNullOrEmpty(tzid))
		{
			return null;
		}
		try
		{
			TimeZoneInfo.FindSystemTimeZoneById(tzid);
			return tzid;
		}
		catch (TimeZoneNotFoundException)
		{
			return null;
		}
		catch (InvalidTimeZoneException)
		{
			return null;
		}
	}

	private static DateTimeOffset ParseDateTime(IReadOnlyDictionary<string, string> parameters, string value)
	{
		if (ParamEquals(parameters, "VALUE", "DATE") || (value.Length == 8 && !value.Contains('T')))
		{
			return new DateTimeOffset(DateTime.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture), TimeSpan.Zero);
		}
		// RFC 5234 §2.3: a quoted ABNF literal is case-insensitive unless marked %s, which
		// RFC 5545 never does for the UTC designator — a compliant server may send a lowercase
		// 'z' just as validly as 'Z'. A case-sensitive check here would fall through to the
		// floating-time branch below, which then throws (the trailing lowercase 'z' doesn't fit
		// that branch's own format string either).
		if (value.Length > 0 && (value[^1] == 'Z' || value[^1] == 'z'))
		{
			return DateTime.ParseExact(value[..^1], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
		}
		var local = DateTime.ParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None);
		if (parameters.GetValueOrDefault("TZID") is { } tzid)
		{
			try
			{
				var zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
				var offset = zone.GetUtcOffset(local);
				return new DateTimeOffset(local, offset);
			}
			catch (TimeZoneNotFoundException)
			{
				// Falls through to floating-time treatment: better an approximate absolute
				// instant than a thrown exception during sync.
			}
			catch (InvalidTimeZoneException)
			{
				// As above.
			}
		}
		return new DateTimeOffset(local, TimeSpan.Zero);
	}

	private static string FormatDateTimeProperty(string name, DateTimeOffset value, bool isAllDay, string? tzid) =>
		$"{RecurrenceSetPropertyName(name, isAllDay, tzid)}:{FormatDateTimeValue(value, isAllDay, tzid)}";

	/// <summary>
	/// The <c>NAME;PARAM=...</c> half of a date-time property line, shared by <c>DTSTART</c>/
	/// <c>DTEND</c>/<c>RECURRENCE-ID</c> and by <c>RDATE</c>/<c>EXDATE</c> — RFC 5545 §3.8.5.1/
	/// §3.8.5.2 require an EXDATE/RDATE to carry the exact same <c>VALUE</c> type and <c>TZID</c>
	/// as <c>DTSTART</c>, since an occurrence excluded/added in the wrong zone or value type
	/// simply fails to match the instance a receiving client generated from <c>RRULE</c>.
	/// </summary>
	private static string RecurrenceSetPropertyName(string name, bool isAllDay, string? tzid) =>
		isAllDay ? $"{name};VALUE=DATE" : tzid is { Length: > 0 } ? $"{name};TZID={tzid}" : name;

	private static string FormatDateTimeValue(DateTimeOffset value, bool isAllDay, string? tzid)
	{
		if (isAllDay)
		{
			return value.ToString("yyyyMMdd");
		}
		if (tzid is { Length: > 0 })
		{
			return FormatLocal(value, tzid);
		}
		return $"{value.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
	}

	private static string FormatLocal(DateTimeOffset value, string tzid)
	{
		try
		{
			var zone = TimeZoneInfo.FindSystemTimeZoneById(tzid);
			var local = TimeZoneInfo.ConvertTime(value, zone);
			return local.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
		}
		catch (TimeZoneNotFoundException)
		{
			return value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
		}
		catch (InvalidTimeZoneException)
		{
			return value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
		}
	}

	private static string Escape(string value) =>
		value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

	/// <summary>
	/// RFC 5545 §3.2's param-value grammar has no backslash-escaping at all — that mechanism
	/// belongs to TEXT-valued properties (<see cref="Escape"/>), a different part of the spec.
	/// A parameter value is either bare (SAFE-CHAR, excluding COMMA/SEMICOLON/COLON/DQUOTE) or a
	/// quoted-string (DQUOTE *QSAFE-CHAR DQUOTE, QSAFE-CHAR itself excluding DQUOTE). Applying
	/// <see cref="Escape"/> to a CN value produced syntax no compliant reader accepts — this
	/// codebase's own reader only "round-tripped" it because <see cref="Unescape"/> silently
	/// undid the same mistake, but every other client (and this reader, reading a real server's
	/// correctly-quoted CN) saw the literal backslashes. DQUOTE has no valid representation in
	/// either grammar, so it is dropped rather than escaped.
	/// </summary>
	private static string QuoteParamValue(string value)
	{
		var sanitized = value.Replace("\"", "");
		return sanitized.IndexOfAny([',', ';', ':']) >= 0 ? $"\"{sanitized}\"" : sanitized;
	}

	/// <summary>
	/// Reverses <see cref="Escape"/> for the TEXT-valued properties it applies to (RFC 5545
	/// §3.3.11). Never applied to RRULE/RDATE/EXDATE, whose commas are structural list
	/// separators rather than escaped text — unescaping those would corrupt them.
	/// </summary>
	[return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
	private static string? Unescape(string? value)
	{
		if (value is null)
		{
			return null;
		}

		var result = new System.Text.StringBuilder(value.Length);
		for (var i = 0; i < value.Length; i++)
		{
			if (value[i] == '\\' && i + 1 < value.Length)
			{
				i++;
				result.Append(value[i] switch
				{
					'n' or 'N' => '\n',
					',' => ',',
					';' => ';',
					'\\' => '\\',
					var other => other,
				});
				continue;
			}
			result.Append(value[i]);
		}
		return result.ToString();
	}

	private static (string Name, IReadOnlyDictionary<string, string> Params, string Value) ParseLine(string line)
	{
		var colon = IndexOfOutsideQuotes(line, ':');
		if (colon < 0)
		{
			return (line, NoParams, string.Empty);
		}
		var head = line[..colon];
		if (head.Length > MaximumPropertyHeadLength)
		{
			throw new InvalidDataException($"Calendar property parameters exceed the {MaximumPropertyHeadLength}-character limit.");
		}
		var value = line[(colon + 1)..];
		var segments = SplitOutsideQuotes(head, ';', MaximumParametersPerProperty + 1);
		var name = segments[0].ToUpperInvariant();
		if (segments.Count == 1)
		{
			return (name, NoParams, value);
		}

		var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var segment in segments.Skip(1))
		{
			var eq = segment.IndexOf('=');
			if (eq > 0)
			{
				parameters[segment[..eq].ToUpperInvariant()] = segment[(eq + 1)..].Trim('"');
			}
		}
		return (name, parameters, value);
	}

	/// <summary>
	/// A property line's parameter list can itself carry <c>;</c> and <c>:</c> characters inside
	/// a quoted param-value (e.g. <c>CN="Doe; Smith"</c>, RFC 5545 §3.2) — splitting on the raw
	/// character, as a naive parser would, corrupts the value and can even misidentify which
	/// colon separates the parameter list from the property's actual value. These two helpers
	/// track whether a scan position is inside a double-quoted span and never split there.
	/// </summary>
	private static int IndexOfOutsideQuotes(string s, char target)
	{
		var quoted = false;
		for (var i = 0; i < s.Length; i++)
		{
			if (s[i] == '"')
			{
				quoted = !quoted;
			}
			else if (s[i] == target && !quoted)
			{
				return i;
			}
		}
		return -1;
	}

	private static List<string> SplitOutsideQuotes(string s, char delimiter, int maximumParts)
	{
		var parts = new List<string>();
		var quoted = false;
		var start = 0;
		for (var i = 0; i < s.Length; i++)
		{
			if (s[i] == '"')
			{
				quoted = !quoted;
			}
			else if (s[i] == delimiter && !quoted)
			{
				if (parts.Count == maximumParts - 1)
				{
					throw new InvalidDataException($"Calendar property exceeds the {maximumParts - 1}-parameter limit.");
				}
				parts.Add(s[start..i]);
				start = i + 1;
			}
		}
		if (parts.Count == maximumParts)
		{
			throw new InvalidDataException($"Calendar property exceeds the {maximumParts - 1}-parameter limit.");
		}
		parts.Add(s[start..]);
		return parts;
	}

	/// <summary>RFC 5545 line unfolding: a CRLF followed by a space or tab continues the prior line.</summary>
	private static List<string> Unfold(string ics)
	{
		var lineBreaks = 0;
		for (var i = 0; i < ics.Length; i++)
		{
			if (ics[i] == '\r')
			{
				lineBreaks++;
				if (i + 1 < ics.Length && ics[i + 1] == '\n')
				{
					i++;
				}
			}
			else if (ics[i] == '\n')
			{
				lineBreaks++;
			}
			if (lineBreaks > MaximumUnfoldedLines)
			{
				throw new InvalidDataException($"Calendar data exceeds the {MaximumUnfoldedLines}-line limit.");
			}
		}
		var raw = ics.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
		var lines = new List<string>();
		StringBuilder? folded = null;
		foreach (var line in raw)
		{
			if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && lines.Count > 0)
			{
				folded ??= new StringBuilder(lines[^1]);
				folded.Append(line, 1, line.Length - 1);
				continue;
			}

			if (folded is not null)
			{
				lines[^1] = folded.ToString();
				folded = null;
			}
			if (line.Length > 0)
			{
				lines.Add(line);
			}
		}
		if (folded is not null)
		{
			lines[^1] = folded.ToString();
		}
		return lines;
	}
}
