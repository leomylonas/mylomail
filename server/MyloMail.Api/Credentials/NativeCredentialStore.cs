using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyloMail.Api.Credentials;

/// <summary>Production OS-backed credential storage (§4).</summary>
public sealed class NativeCredentialStore(string dataDirectory) : ICredentialStore
{
	private const string Service = "MyloMail";
	private readonly string windowsDirectory = Path.Combine(dataDirectory, "credentials");

	public static async Task<bool> IsAvailableAsync(string dataDirectory, CancellationToken ct)
	{
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
		if (OperatingSystem.IsWindows())
		{
			Directory.CreateDirectory(windowsDirectory);
			var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(encoded), null, DataProtectionScope.CurrentUser);
			await File.WriteAllBytesAsync(Path.Combine(windowsDirectory, accountId.ToString("N")), protectedBytes, ct);
			return;
		}

		if (OperatingSystem.IsMacOS())
		{
			await RunAsync("security", $"add-generic-password -U -s {Service} -a {accountId:N} -w {encoded}", ct);
			return;
		}

		await RunAsync("secret-tool", $"store --label={Service} service {Service} account {accountId:N}", ct, encoded);
	}

	public async Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct)
	{
		string? encoded;
		if (OperatingSystem.IsWindows())
		{
			var path = Path.Combine(windowsDirectory, accountId.ToString("N"));
			if (!File.Exists(path)) return null;
			encoded = Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(path, ct), null, DataProtectionScope.CurrentUser));
		}
		else if (OperatingSystem.IsMacOS())
		{
			encoded = await RunAsync("security", $"find-generic-password -s {Service} -a {accountId:N} -w", ct, allowMissing: true);
		}
		else
		{
			encoded = await RunAsync("secret-tool", $"lookup service {Service} account {accountId:N}", ct, allowMissing: true);
		}

		return string.IsNullOrWhiteSpace(encoded)
			? null
			: JsonSerializer.Deserialize<CredentialPayload>(Convert.FromBase64String(encoded.Trim()));
	}

	public async Task DeleteAsync(Guid accountId, CancellationToken ct)
	{
		if (OperatingSystem.IsWindows())
		{
			File.Delete(Path.Combine(windowsDirectory, accountId.ToString("N")));
			return;
		}
		if (OperatingSystem.IsMacOS())
		{
			await RunAsync("security", $"delete-generic-password -s {Service} -a {accountId:N}", ct, allowMissing: true);
			return;
		}
		await RunAsync("secret-tool", $"clear service {Service} account {accountId:N}", ct, allowMissing: true);
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
