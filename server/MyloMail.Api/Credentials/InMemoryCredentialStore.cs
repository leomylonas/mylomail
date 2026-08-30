namespace MyloMail.Api.Credentials;

/// <summary>
/// Process-local credential store for provider transport tests. It is intentionally not a
/// production registration: losing the process loses the cache, which makes accidental use
/// visible instead of silently weakening the durable-store requirement in §4.
/// </summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
	private readonly Dictionary<Guid, CredentialPayload> payloads = [];
	private readonly object gate = new();

	public Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		lock (gate)
		{
			payloads[accountId] = new CredentialPayload(payload.Format, [.. payload.Data]);
		}

		return Task.CompletedTask;
	}

	public Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		lock (gate)
		{
			return Task.FromResult(
				payloads.TryGetValue(accountId, out var payload)
					? new CredentialPayload(payload.Format, [.. payload.Data])
					: null
			);
		}
	}

	public Task DeleteAsync(Guid accountId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		lock (gate)
		{
			payloads.Remove(accountId);
		}

		return Task.CompletedTask;
	}
}
