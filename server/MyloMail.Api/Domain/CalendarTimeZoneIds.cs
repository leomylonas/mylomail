namespace MyloMail.Api.Domain;

/// <summary>Maps Windows identifiers to IANA while preserving each valid IANA identity.</summary>
public static class CalendarTimeZoneIds
{
	public static string? Canonicalize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var candidate = value.Trim();
		if (TimeZoneInfo.TryConvertWindowsIdToIanaId(candidate, out var fromWindows))
		{
			return fromWindows;
		}
		return candidate;
	}
}
