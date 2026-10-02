using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyloMail.Api.Credentials;

/// <summary>Production OS-backed credential storage (§4).</summary>
public sealed class NativeCredentialStore(string dataDirectory) : ICredentialStore
{
	private const string Service = "MyloMail";

	/// <summary>
	/// Encoded credentials already read (or written) by this process. Every provider call asks
	/// for its account's credential, and with background download that is thousands of reads an
	/// hour; each one a fresh connection to the OS store, which both prompts a locked store
	/// repeatedly and hammers a service that has been seen to abort when a client disconnects
	/// mid-request. The credential is already held in memory for the connection that uses it, so
	/// holding the encoded form for the process lifetime adds no exposure. Never caches a failure.
	/// </summary>
	private static readonly ConcurrentDictionary<(string Directory, Guid AccountId), string> Cache = new();
	private readonly string windowsDirectory = Path.Combine(dataDirectory, "credentials");

	/// <summary>
	/// Asks the desktop to unlock the credential store, showing its own prompt. Only the Linux
	/// Secret Service can be locked independently of the app; Windows and macOS prompt (or not)
	/// at the point of use, so there is nothing to do ahead of time there.
	/// </summary>
	public static async Task UnlockAsync(CancellationToken ct)
	{
		if (!OperatingSystem.IsLinux()) return;
		try
		{
			await LinuxSecretServiceCredentialStore.UnlockDefaultCollectionAsync(ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			throw new CredentialStoreUnavailableException($"The OS credential store could not be unlocked: {ex.Message}", ex);
		}
	}

	public static async Task<bool> IsAvailableAsync(string dataDirectory, CancellationToken ct)
	{
		// A locked Secret Service collection is present but temporarily inaccessible, not absent.
		// Probing it by writing a secret conflates those states and silently switches an existing
		// account to the empty master-password store. Test only service/collection presence here;
		// point-of-use reads still report a locked collection as CredentialStoreUnavailable.
		if (OperatingSystem.IsLinux())
		{
			return await LinuxSecretServiceCredentialStore.IsAvailableAsync(ct);
		}

		var store = new NativeCredentialStore(dataDirectory);
		var probeId = Guid.NewGuid();
		try
		{
			await store.StoreAsync(probeId, new CredentialPayload("availability-probe", [1]), ct);
			var result = await store.RetrieveAsync(probeId, ct);
			return result is { Format: "availability-probe" };
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			return false;
		}
		finally
		{
			try { await store.DeleteAsync(probeId, CancellationToken.None); }
			catch (Exception) { /* A failed probe must not make startup fail while cleaning up. */ }
		}
	}

	public async Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct)
	{
		var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
		try
		{
			if (OperatingSystem.IsWindows())
			{
				Directory.CreateDirectory(windowsDirectory);
				var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(encoded), null, DataProtectionScope.CurrentUser);
				await File.WriteAllBytesAsync(Path.Combine(windowsDirectory, accountId.ToString("N")), protectedBytes, ct);
				Cache[(dataDirectory, accountId)] = encoded;
				return;
			}

			if (OperatingSystem.IsMacOS())
			{
				MacKeychainCredentialStore.Store(Service, accountId, encoded, ct);
				Cache[(dataDirectory, accountId)] = encoded;
				return;
			}

			await LinuxSecretServiceCredentialStore.StoreAsync(Service, accountId, encoded, ct);
			Cache[(dataDirectory, accountId)] = encoded;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Same reasoning as RetrieveAsync below: a locked keyring with no interactive unlock
			// offered, or a denied Keychain prompt, means the store itself could not be reached
			// -- distinct from any credential-content problem (§4).
			throw new CredentialStoreUnavailableException(
				$"The OS credential store could not be reached: {ex.Message}",
				ex
			);
		}
	}

	public async Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct)
	{
		if (Cache.TryGetValue((dataDirectory, accountId), out var cached)) return Decode(cached);

		string? encoded;
		try
		{
			if (OperatingSystem.IsWindows())
			{
				var path = Path.Combine(windowsDirectory, accountId.ToString("N"));
				if (!File.Exists(path)) return null;
				encoded = Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, ct), null, DataProtectionScope.CurrentUser));
			}
			else if (OperatingSystem.IsMacOS())
			{
				encoded = MacKeychainCredentialStore.Retrieve(Service, accountId, ct);
			}
			else
			{
				encoded = await LinuxSecretServiceCredentialStore.RetrieveAsync(Service, accountId, ct);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Not "no credential stored" — every implementation above already returns null for
			// that (a missing file, ItemNotFound, an empty Secret Service search result). An
			// exception here means the store itself couldn't be reached: a locked keyring
			// needing an interactive unlock (LinuxSecretServiceCredentialStore.EnsureNoPrompt),
			// a denied Keychain prompt, no D-Bus session. Reauthenticating cannot fix any of
			// these — the stored credential may well be fine — so this is reported distinctly
			// from ProviderAuthenticationException (§4).
			throw new CredentialStoreUnavailableException(
				$"The OS credential store could not be reached: {ex.Message}",
				ex
			);
		}

		if (string.IsNullOrWhiteSpace(encoded)) return null;
		Cache[(dataDirectory, accountId)] = encoded;
		return Decode(encoded);
	}

	private static CredentialPayload? Decode(string encoded) =>
		JsonSerializer.Deserialize<CredentialPayload>(Convert.FromBase64String(encoded.Trim()));

	public async Task DeleteAsync(Guid accountId, CancellationToken ct)
	{
		Cache.TryRemove((dataDirectory, accountId), out _);
		try
		{
			if (OperatingSystem.IsWindows())
			{
				File.Delete(Path.Combine(windowsDirectory, accountId.ToString("N")));
				return;
			}
			if (OperatingSystem.IsMacOS())
			{
				MacKeychainCredentialStore.Delete(Service, accountId, ct);
				return;
			}
			await LinuxSecretServiceCredentialStore.DeleteAsync(Service, accountId, ct);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Same reasoning as RetrieveAsync/StoreAsync above.
			throw new CredentialStoreUnavailableException(
				$"The OS credential store could not be reached: {ex.Message}",
				ex
			);
		}
	}

	private static async Task<string?> RunAsync(string file, string arguments, CancellationToken ct, string? input = null, bool allowMissing = false)
	{
		using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardInput = input is not null, RedirectStandardError = true, UseShellExecute = false })
			?? throw new InvalidOperationException($"Could not start {file}.");
		if (input is not null) await process.StandardInput.WriteAsync(input);
		if (input is not null) process.StandardInput.Close();
		var output = await process.StandardOutput.ReadToEndAsync(ct);
		var error = await process.StandardError.ReadToEndAsync(ct);
		await process.WaitForExitAsync(ct);
		if (process.ExitCode != 0 && !allowMissing) throw new InvalidOperationException($"{file} failed: {error}");
		return process.ExitCode == 0 ? output : null;
	}
}
