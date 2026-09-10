namespace MyloMail.Api.Sync;

/// <summary>Serializes observation of an account's provider change stream.</summary>
/// <remarks>
/// Gmail history is account-scoped; concurrent baseline walks could commit a cursor beyond a
/// coverage snapshot. The lease is process-local by design: durable rebase state recovers
/// after a crash, while this only excludes live workers in the one backend process.
/// </remarks>
public sealed class ChangeStreamGate
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
