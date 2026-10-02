namespace MyloMail.Api.Scheduling;

/// <summary>
/// Messages the user wants now — one they have opened, rows scrolled into view, downloads they
/// asked to retry — ahead of the bulk backlog. In memory only: it is a hint about what to fetch
/// next, not work to be recovered, and the durable queue (`MessageContentState`) still holds
/// everything. Latest request first.
/// </summary>
internal sealed class ContentPriority
{
	private const int PerAccountLimit = 500;
	private readonly Dictionary<Guid, List<Guid>> byAccount = [];

	public void Add(Guid accountId, IEnumerable<Guid> messageIds)
	{
		lock (byAccount)
		{
			if (!byAccount.TryGetValue(accountId, out var ids)) byAccount[accountId] = ids = [];
			foreach (var id in messageIds)
			{
				ids.Remove(id);
				ids.Add(id);
			}
			if (ids.Count > PerAccountLimit) ids.RemoveRange(0, ids.Count - PerAccountLimit);
		}
	}

	/// <summary>The most recently requested message that is still pending, forgetting those that are not.</summary>
	public Guid? Next(Guid accountId, IReadOnlySet<Guid> pending)
	{
		lock (byAccount)
		{
			if (!byAccount.TryGetValue(accountId, out var ids)) return null;
			ids.RemoveAll(id => !pending.Contains(id));
			return ids.Count > 0 ? ids[^1] : null;
		}
	}
}
