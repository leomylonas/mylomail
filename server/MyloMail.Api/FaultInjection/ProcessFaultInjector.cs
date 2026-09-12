using System.Diagnostics;
using System.Text;

namespace MyloMail.Api.FaultInjection;

/// <summary>
/// Test-only injector that terminates the backend process at one named durable boundary.
/// It is enabled only in the explicit <c>FaultInjection</c> host environment.
/// </summary>
public sealed class ProcessFaultInjector : IFaultInjector
{
	public const string PointEnvironmentVariable = "MYLOMAIL_FAULT_POINT";
	public const string MarkerEnvironmentVariable = "MYLOMAIL_FAULT_MARKER";
	public const string AcknowledgementEnvironmentVariable = "MYLOMAIL_FAULT_ACK";
	public const string DataDirectoryEnvironmentVariable = "MYLOMAIL_FAULT_DATA_DIRECTORY";
	private readonly string armedPoint;
	private readonly string markerPath;
	private readonly string? acknowledgementPath;
	private int fired;

	private ProcessFaultInjector(
		string armedPoint,
		string markerPath,
		string? acknowledgementPath
	)
	{
		this.armedPoint = armedPoint;
		this.markerPath = markerPath;
		this.acknowledgementPath = acknowledgementPath;
	}

	public static string? DataDirectoryOverrideFromEnvironment()
	{
		if (!IsFaultInjectionEnvironment())
		{
			return null;
		}

		var dataDirectory = Environment.GetEnvironmentVariable(
			DataDirectoryEnvironmentVariable
		);
		if (string.IsNullOrWhiteSpace(dataDirectory))
		{
			return null;
		}
		if (!Path.IsPathFullyQualified(dataDirectory))
		{
			throw new InvalidOperationException(
				$"{DataDirectoryEnvironmentVariable} must be a fully qualified path when set."
			);
		}
		return dataDirectory;
	}

	public static ProcessFaultInjector? FromEnvironment()
	{
		var point = Environment.GetEnvironmentVariable(PointEnvironmentVariable);
		if (
			!IsFaultInjectionEnvironment()
			|| string.IsNullOrWhiteSpace(point)
		)
		{
			return null;
		}

		var marker = Environment.GetEnvironmentVariable(MarkerEnvironmentVariable);
		if (string.IsNullOrWhiteSpace(marker) || !Path.IsPathFullyQualified(marker))
		{
			throw new InvalidOperationException(
				$"{MarkerEnvironmentVariable} must be a fully qualified path when process fault injection is enabled."
			);
		}

		var acknowledgement = Environment.GetEnvironmentVariable(
			AcknowledgementEnvironmentVariable
		);
		if (
			!string.IsNullOrWhiteSpace(acknowledgement)
			&& !Path.IsPathFullyQualified(acknowledgement)
		)
		{
			throw new InvalidOperationException(
				$"{AcknowledgementEnvironmentVariable} must be a fully qualified path when set."
			);
		}

		return new ProcessFaultInjector(point, marker, acknowledgement);
	}

	private static bool IsFaultInjectionEnvironment()
	{
		var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
			?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
		return string.Equals(
			environment,
			"FaultInjection",
			StringComparison.Ordinal
		);
	}

	public void Reached(string point)
	{
		if (
			!string.Equals(point, armedPoint, StringComparison.Ordinal)
			|| Interlocked.Exchange(ref fired, 1) != 0
		)
		{
			return;
		}

		Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
		using (var stream = new FileStream(
			markerPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.Read,
			bufferSize: 4096,
			FileOptions.WriteThrough
		))
		{
			var bytes = Encoding.UTF8.GetBytes(point);
			stream.Write(bytes);
			stream.Flush(flushToDisk: true);
		}

		if (acknowledgementPath is not null)
		{
			var deadline = Stopwatch.GetTimestamp() + (10 * Stopwatch.Frequency);
			while (
				!File.Exists(acknowledgementPath)
				&& Stopwatch.GetTimestamp() < deadline
			)
			{
				Thread.Sleep(5);
			}
		}

		Process.GetCurrentProcess().Kill(entireProcessTree: false);
		Thread.Sleep(Timeout.Infinite);
	}
}
