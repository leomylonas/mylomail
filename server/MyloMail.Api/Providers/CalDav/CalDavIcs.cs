using System.Globalization;
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
internal static class CalDavIcs
{
	public static IReadOnlyList<CalendarEventDto> ParseEvents(string ics, string href, string etag)
	{
		var lines = Unfold(ics);
		var events = new List<CalendarEventDto>();
		List<(string Name, IReadOnlyDictionary<string, string> Params, string Value)>? current = null;

		foreach (var line in lines)
		{
			if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
			{
				current = [];
				continue;
			}
			if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
			{
				if (current is not null)
				{
					events.Add(ToDto(current, href, etag));
				}
				current = null;
				continue;
			}
			if (current is not null)
			{
				current.Add(ParseLine(line));
			}
		}

		return events;
	}

	public static string ToIcs(string uid, CalendarEventDto ev)
	{
		var lines = new List<string> { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//MyloMail//CalDAV//EN", "BEGIN:VEVENT" };
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
			var cn = organizer.Name is { Length: > 0 } ? $";CN={Escape(organizer.Name)}" : "";
			lines.Add($"ORGANIZER{cn}:mailto:{organizer.Email}");
		}
		foreach (var attendee in ev.Attendees)
		{
			var cn = attendee.Name is { Length: > 0 } ? $";CN={Escape(attendee.Name)}" : "";
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
			lines.Add($"RDATE:{string.Join(',', ev.RecurrenceDates.Select(d => FormatDateTimeValue(d, ev.IsAllDay, null)))}");
		}
		if (ev.ExceptionDates.Count > 0)
		{
			lines.Add($"EXDATE:{string.Join(',', ev.ExceptionDates.Select(d => FormatDateTimeValue(d, ev.IsAllDay, null)))}");
		}
		if (ev.RecurrenceId is { } recurrenceId)
		{
			lines.Add(FormatDateTimeProperty("RECURRENCE-ID", recurrenceId, ev.IsAllDay, ev.StartTimeZoneId));
		}
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

		var uid = Single("UID") ?? href;
		var isAllDay = SingleWithParams("DTSTART")?.Params.GetValueOrDefault("VALUE") == "DATE";
		var start = SingleWithParams("DTSTART") is { } dtstart ? ParseDateTime(dtstart.Params, dtstart.Value) : DateTimeOffset.UnixEpoch;
		var end = SingleWithParams("DTEND") is { } dtend
			? ParseDateTime(dtend.Params, dtend.Value)
			: start.AddHours(1);
		var recurrenceId = SingleWithParams("RECURRENCE-ID") is { } rid ? ParseDateTime(rid.Params, rid.Value) : (DateTimeOffset?)null;
		var providerEventId = recurrenceId is null ? href : $"{href}#{recurrenceId:O}";

		return new CalendarEventDto
		{
			ProviderEventId = providerEventId,
			ICalUid = uid,
			ProviderRevision = etag,
			Sequence = int.TryParse(Single("SEQUENCE"), out var seq) ? seq : 0,
			Title = Unescape(Single("SUMMARY")) ?? string.Empty,
			Location = Unescape(Single("LOCATION")),
			Description = Unescape(Single("DESCRIPTION")),
			Start = start,
			End = end,
			StartTimeZoneId = SingleWithParams("DTSTART")?.Params.GetValueOrDefault("TZID"),
			EndTimeZoneId = SingleWithParams("DTEND")?.Params.GetValueOrDefault("TZID"),
			IsAllDay = isAllDay,
			Organizer = SingleWithParams("ORGANIZER") is { } organizer
				? new Address(Unescape(organizer.Params.GetValueOrDefault("CN")), StripMailto(organizer.Value))
				: null,
			Attendees = All("ATTENDEE")
				.Select(a => new Attendee(
					Unescape(a.Params.GetValueOrDefault("CN")),
					StripMailto(a.Value),
					a.Params.GetValueOrDefault("ROLE") switch
					{
						"OPT-PARTICIPANT" => AttendeeRole.Optional,
						"NON-PARTICIPANT" => AttendeeRole.Resource,
						_ => AttendeeRole.Required,
					},
					a.Params.GetValueOrDefault("PARTSTAT") switch
					{
						"ACCEPTED" => ResponseStatus.Accepted,
						"DECLINED" => ResponseStatus.Declined,
						"TENTATIVE" => ResponseStatus.Tentative,
						_ => ResponseStatus.NeedsAction,
					}
				))
				.ToList(),
			Status = Single("STATUS") switch
			{
				"TENTATIVE" => EventStatus.Tentative,
				"CANCELLED" => EventStatus.Cancelled,
				_ => EventStatus.Confirmed,
			},
			Reminders = All("TRIGGER")
				.Select(t => TryParseTrigger(t.Params, t.Value, start))
				.Where(r => r is not null)
				.Select(r => r!.Value)
				.ToList(),
			RecurrenceRules = All("RRULE").Select(r => r.Value).ToList(),
			RecurrenceDates = All("RDATE").SelectMany(r => r.Value.Split(',')).Select(v => ParseDateTime(NoParams, v)).ToList(),
			ExceptionDates = All("EXDATE").SelectMany(r => r.Value.Split(',')).Select(v => ParseDateTime(NoParams, v)).ToList(),
			RecurrenceMasterProviderEventId = recurrenceId is null ? null : href,
			RecurrenceId = recurrenceId,
		};
	}

	private static readonly IReadOnlyDictionary<string, string> NoParams = new Dictionary<string, string>();

	private static string StripMailto(string value) =>
		value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value["mailto:".Length..] : value;

	private static DateTimeOffset? TryParseTrigger(IReadOnlyDictionary<string, string> parameters, string value, DateTimeOffset start)
	{
		if (parameters.GetValueOrDefault("VALUE") == "DATE-TIME")
		{
			return ParseDateTime(parameters, value);
		}
		return TryParseDuration(value, out var duration) ? start + duration : null;
	}

	/// <summary>A minimal ISO-8601 duration parser covering the subset VALARM triggers use.</summary>
	private static bool TryParseDuration(string value, out TimeSpan duration)
	{
		duration = TimeSpan.Zero;
		var span = value.AsSpan();
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
				number = number * 10 + (c - '0');
				hasNumber = true;
				continue;
			}
			if (!hasNumber)
			{
				return false;
			}
			total += c switch
			{
				'D' => TimeSpan.FromDays(number),
				'W' => TimeSpan.FromDays(number * 7),
				'H' => TimeSpan.FromHours(number),
				'M' => inTime ? TimeSpan.FromMinutes(number) : TimeSpan.Zero,
				'S' => TimeSpan.FromSeconds(number),
				_ => TimeSpan.Zero,
			};
			number = 0;
			hasNumber = false;
		}
		duration = negative ? -total : total;
		return true;
	}

	private static DateTimeOffset ParseDateTime(IReadOnlyDictionary<string, string> parameters, string value)
	{
		if (parameters.GetValueOrDefault("VALUE") == "DATE" || (value.Length == 8 && !value.Contains('T')))
		{
			return new DateTimeOffset(DateTime.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture), TimeSpan.Zero);
		}
		if (value.EndsWith('Z'))
		{
			return DateTime.ParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
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

	private static string FormatDateTimeProperty(string name, DateTimeOffset value, bool isAllDay, string? tzid)
	{
		if (isAllDay)
		{
			return $"{name};VALUE=DATE:{value:yyyyMMdd}";
		}
		if (tzid is { Length: > 0 })
		{
			return $"{name};TZID={tzid}:{FormatLocal(value, tzid)}";
		}
		return $"{name}:{value.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";
	}

	private static string FormatDateTimeValue(DateTimeOffset value, bool isAllDay, string? tzid) =>
		isAllDay ? value.ToString("yyyyMMdd") : $"{value.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}";

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
		value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");

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
		var colon = line.IndexOf(':');
		if (colon < 0)
		{
			return (line, NoParams, string.Empty);
		}
		var head = line[..colon];
		var value = line[(colon + 1)..];
		var segments = head.Split(';');
		var name = segments[0].ToUpperInvariant();
		if (segments.Length == 1)
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

	/// <summary>RFC 5545 line unfolding: a CRLF followed by a space or tab continues the prior line.</summary>
	private static List<string> Unfold(string ics)
	{
		var raw = ics.Replace("\r\n", "\n").Split('\n');
		var lines = new List<string>();
		foreach (var line in raw)
		{
			if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && lines.Count > 0)
			{
				lines[^1] += line[1..];
			}
			else if (line.Length > 0)
			{
				lines.Add(line);
			}
		}
		return lines;
	}
}
