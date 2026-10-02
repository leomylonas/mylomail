using System.Collections.Concurrent;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Admits one runner per key. A caller refused entry is remembered, so the runner can tell on
/// exit that it should go round once more rather than silently dropping the refused caller's work.
/// </summary>
internal sealed class SingleFlight
{
	private readonly ConcurrentDictionary<Guid, bool> running = new();

	public bool TryEnter(Guid key)
	{
		lock (running)
		{
			if (running.TryAdd(key, false)) return true;
			running[key] = true;
			return false;
		}
	}

	/// <returns>True when another caller was turned away while this one ran.</returns>
	public bool Exit(Guid key)
	{
		lock (running)
		{
			return running.TryRemove(key, out var rerun) && rerun;
		}
	}
}
