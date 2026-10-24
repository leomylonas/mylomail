using System.Text;

namespace MyloMail.Api.Export;

/// <summary>Names for exported folders and files that every common filesystem accepts.</summary>
internal static class ExportNames
{
	private const int MaximumSegmentLength = 100;
	private static readonly HashSet<char> Invalid = [.. "<>:\"/\\|?*"];

	/// <summary>One path segment from arbitrary text: no separators, control or Windows-reserved characters, no reserved device names, never empty.</summary>
	public static string Segment(string text, string fallback)
	{
		var builder = new StringBuilder(text.Length);
		foreach (var c in text)
		{
			builder.Append(char.IsControl(c) || Invalid.Contains(c) ? '_' : c);
		}

		var candidate = builder.ToString().Trim().TrimEnd('.', ' ');
		if (candidate.Length > MaximumSegmentLength)
		{
			candidate = candidate[..MaximumSegmentLength].TrimEnd('.', ' ');
		}

		return candidate.Length == 0 || candidate is "." or ".." || IsDeviceName(candidate) ? fallback : candidate;
	}

	/// <summary>A name not in <paramref name="taken"/> (compared ignoring case, as macOS and Windows do), adding " (2)", " (3)" as needed. The result is added to <paramref name="taken"/>.</summary>
	public static string Unique(string name, ISet<string> taken)
	{
		var candidate = name;
		for (var n = 2; !taken.Add(candidate.ToLowerInvariant()); n++)
		{
			candidate = $"{name} ({n})";
		}

		return candidate;
	}

	/// <summary><c>2026-10-21 1542 - Subject</c>, in UTC so a resumed export names a message the same way.</summary>
	public static string DateAndSubject(DateTimeOffset received, string subject) =>
		$"{received.UtcDateTime:yyyy-MM-dd HHmm} - {Segment(subject, "(no subject)")}";

	private static bool IsDeviceName(string value)
	{
		var stem = value.Split('.')[0].ToUpperInvariant();
		return stem is "CON" or "PRN" or "AUX" or "NUL"
			|| (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
	}
}
