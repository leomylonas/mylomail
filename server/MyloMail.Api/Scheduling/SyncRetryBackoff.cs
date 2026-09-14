using System.Collections.Concurrent;

namespace MyloMail.Api.Scheduling;

/// <summary>Bounded in-process backoff for transient sync failures without provider Retry-After.</summary>
public sealed class SyncRetryBackoff
{
	private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan MaximumDelay = TimeSpan.FromMinutes(5);
	private readonly ConcurrentDictionary<string, int> failures = new(StringComparer.Ordinal);

	public TimeSpan Next(string workKey)
	{
		var attempt = failures.AddOrUpdate(workKey, 1, (_, current) => Math.Min(current + 1, 7));
		var multiplier = 1 << (attempt - 1);
		return TimeSpan.FromTicks(Math.Min(InitialDelay.Ticks * multiplier, MaximumDelay.Ticks));
	}

	public void Reset(string workKey) => failures.TryRemove(workKey, out _);
}
