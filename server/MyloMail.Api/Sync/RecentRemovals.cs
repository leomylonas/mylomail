using System.Collections.Concurrent;

namespace MyloMail.Api.Sync;

/// <summary>
/// Where this process has just removed an occurrence, so that a sync page fetched <i>before</i> the
/// removal cannot put it back.
/// </summary>
/// <remarks>
/// A delete is applied locally the moment the server confirms it. A poll that asked the server a
/// moment earlier still lists the message, and if it is applied afterwards it recreates the
/// occurrence: the message reappears in its folder until the next poll takes it away again. The page
/// is stale, not the removal, so an occurrence removed after a page was requested is not applied from
/// that page. A page requested after the removal is trusted as ever.
/// <para>
/// Process-local on purpose. The window it closes is seconds long and lies inside one run; nothing
/// durable depends on it, and a skipped observation is always one the server has since superseded.
/// </para>
/// </remarks>
public static class RecentRemovals
{
	private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
	private static readonly ConcurrentDictionary<(Guid MailboxId, string OccurrenceId), DateTimeOffset> removed = new();

	public static void Record(Guid mailboxId, string providerOccurrenceId)
	{
		var now = DateTimeOffset.UtcNow;
		removed[(mailboxId, providerOccurrenceId)] = now;
		if (removed.Count > 1024)
		{
			foreach (var (key, at) in removed)
			{
				if (now - at > Retention)
				{
					removed.TryRemove(key, out _);
				}
			}
		}
	}

	/// <summary>Whether this occurrence was removed here after <paramref name="requestedAt"/>.</summary>
	public static bool RemovedSince(Guid mailboxId, string providerOccurrenceId, DateTimeOffset requestedAt) =>
		removed.TryGetValue((mailboxId, providerOccurrenceId), out var at) && at > requestedAt;
}
