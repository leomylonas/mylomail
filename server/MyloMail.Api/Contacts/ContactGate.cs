namespace MyloMail.Api.Contacts;

/// <summary>
/// Serializes provider contact observation and mutation for one account in the sole backend
/// process. Durable operations remain the authority across process restarts.
/// </summary>
public sealed class ContactGate
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
