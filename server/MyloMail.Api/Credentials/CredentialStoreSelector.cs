using System.Security.Cryptography;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Credentials;

/// <summary>Selects a usable native store at startup, or the explicitly supplied fallback.</summary>
public sealed class CredentialStoreSelector(string dataDirectory) : IDisposable
{
	private const string MasterPasswordEnvironmentVariable = "MYLOMAIL_MASTER_PASSWORD";
	private bool useNativeStore;
	private byte[]? fallbackKey;

	public async Task InitializeAsync(MyloMailDbContext database, CancellationToken ct)
	{
		useNativeStore = await NativeCredentialStore.IsAvailableAsync(dataDirectory, ct);
		if (useNativeStore) return;

		var masterPassword = Environment.GetEnvironmentVariable(MasterPasswordEnvironmentVariable);
		Environment.SetEnvironmentVariable(MasterPasswordEnvironmentVariable, null);
		if (string.IsNullOrEmpty(masterPassword)) throw new CredentialStoreUnavailableException();

		using var store = new MasterPasswordCredentialStore(database, []);
		await store.InitializeAsync(masterPassword, ct);
		fallbackKey = store.CreateKeyCopy();
	}

	public ICredentialStore Create(MyloMailDbContext database) => useNativeStore
		? new NativeCredentialStore(dataDirectory)
		: new MasterPasswordCredentialStore(database, [.. (fallbackKey ?? throw new InvalidOperationException("Credential store has not been initialized."))]);

	public void Dispose()
	{
		if (fallbackKey is not null) CryptographicOperations.ZeroMemory(fallbackKey);
	}
}
