using System.Globalization;

namespace MyloMail.Api.Persistence;

/// <summary>
/// Reports long startup phases to whoever launched the backend, which has no port to ask
/// before startup finishes. A null fraction means the phase is running but its length is
/// unknown; it is still a sign of life.
/// </summary>
public interface IStartupProgress
{
	void Report(string phase, double? fraction);
}

/// <summary>For callers that have nobody to report to, such as tests.</summary>
public sealed class NullStartupProgress : IStartupProgress
{
	public void Report(string phase, double? fraction) { }
}

/// <summary>
/// One <c>MYLOMAIL_STARTUP=phase:fraction</c> line per report on standard output, the same
/// channel as the port announcement. Electron's launch deadline is measured from the last such
/// line rather than from process start, so a slow backup or migration is not mistaken for a
/// hung backend, and the line also drives the progress shown to the user.
/// </summary>
public sealed class ConsoleStartupProgress : IStartupProgress
{
	public void Report(string phase, double? fraction) =>
		Console.Out.WriteLine(
			$"MYLOMAIL_STARTUP={phase}:{fraction?.ToString("0.###", CultureInfo.InvariantCulture) ?? "-"}"
		);
}
