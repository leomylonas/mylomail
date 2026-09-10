namespace MyloMail.Api.Sync;

/// <summary>
/// Serializes calendar observation and recovery for one account. A recovery snapshot is only
/// meaningful until another sync page can commit a newer view of that account's calendars.
/// </summary>
/// <remarks>
/// Deliberately in-memory: this coordinates concurrent work in the sole backend process; a
/// process crash releases every holder before startup reconciliation replays durable attempts.
/// Entries follow <see cref="MyloMail.Api.Scheduling.AccountGate"/>'s account-lifetime precedent.
/// </remarks>
public sealed class CalendarSyncGate
{
	private readonly Dictionary<Guid, SemaphoreSlim> gates = [];
	private readonly object guard = new();

	public async Task<IDisposable> EnterAsync(Guid accountId, CancellationToken ct)
	{
		SemaphoreSlim gate;
		lock (guard)
		{
			if (!gates.TryGetValue(accountId, out gate!))
			{
				gate = new SemaphoreSlim(1, 1);
				gates.Add(accountId, gate);
			}
		}
		await gate.WaitAsync(ct);
		return new Lease(gate);
	}

	private sealed class Lease(SemaphoreSlim gate) : IDisposable
	{
		public void Dispose() => gate.Release();
	}
}
