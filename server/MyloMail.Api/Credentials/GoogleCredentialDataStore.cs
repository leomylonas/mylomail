using System.Collections.Concurrent;
using System.Text.Json;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace MyloMail.Api.Credentials;

/// <summary>
/// Adapts Google's token-cache hook to the application's credential boundary. Google must
/// never fall back to its default file data store, which would put OAuth tokens outside the
/// selected keychain or master-password store (§5).
/// </summary>
public sealed class GoogleCredentialDataStore(ICredentialStore store, Guid accountId) : IDataStore
{
	private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> AccountLocks = [];
	private const string Format = "google-token-cache-v1";

	public async Task StoreAsync<T>(string key, T value)
	{
		var gate = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			var entries = await ReadAsync(CancellationToken.None);
			if (value is TokenResponse incoming
				&& entries.TryGetValue(key, out var existingBytes)
				&& JsonSerializer.Deserialize<TokenResponse>(existingBytes) is { } existing
				&& HasBroaderScope(existing.Scope, incoming.Scope))
			{
				return;
			}
			entries[key] = JsonSerializer.SerializeToUtf8Bytes(value);
			await WriteAsync(entries, CancellationToken.None);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task DeleteAsync<T>(string key)
	{
		var gate = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			var entries = await ReadAsync(CancellationToken.None);
			entries.Remove(key);
			await WriteAsync(entries, CancellationToken.None);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<T?> GetAsync<T>(string key)
	{
		var gate = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			var entries = await ReadAsync(CancellationToken.None);
			return entries.TryGetValue(key, out var bytes)
				? JsonSerializer.Deserialize<T>(bytes)
				: default;
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task ClearAsync()
	{
		var gate = AccountLocks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync();
		try
		{
			await store.DeleteAsync(accountId, CancellationToken.None);
		}
		finally
		{
			gate.Release();
		}
	}

	private static bool HasBroaderScope(string? existingScope, string? incomingScope)
	{
		if (string.IsNullOrWhiteSpace(existingScope) || string.IsNullOrWhiteSpace(incomingScope))
		{
			return false;
		}
		var existing = existingScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
		return incomingScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(existing.Contains)
			&& existing.Count > incomingScope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Count();
	}

	private async Task<Dictionary<string, byte[]>> ReadAsync(CancellationToken ct)
	{
		var payload = await store.RetrieveAsync(accountId, ct);
		if (payload is null)
		{
			return [];
		}

		if (payload.Format != Format)
		{
			throw new InvalidOperationException(
				$"Credential payload for account '{accountId}' belongs to '{payload.Format}', not Google OAuth."
			);
		}

		return JsonSerializer.Deserialize<Dictionary<string, byte[]>>(payload.Data) ?? [];
	}

	private Task WriteAsync(Dictionary<string, byte[]> entries, CancellationToken ct) =>
		store.StoreAsync(
			accountId,
			new CredentialPayload(Format, JsonSerializer.SerializeToUtf8Bytes(entries)),
			ct
		);
}
