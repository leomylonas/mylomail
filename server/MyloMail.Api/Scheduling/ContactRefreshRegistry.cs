using System.Collections.Concurrent;

namespace MyloMail.Api.Scheduling;

/// <summary>Claims one self-scheduling provider-contact refresh loop per account and process.</summary>
public sealed class ContactRefreshRegistry
{
	private readonly ConcurrentDictionary<Guid, byte> accounts = [];

	public bool TryStart(Guid accountId) => accounts.TryAdd(accountId, 0);
	public void Stop(Guid accountId) => accounts.TryRemove(accountId, out _);
}
