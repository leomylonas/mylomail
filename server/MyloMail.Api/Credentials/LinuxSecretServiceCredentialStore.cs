using System.Text;
using MyloMail.Api.Credentials.SecretService;
using Tmds.DBus.Protocol;

namespace MyloMail.Api.Credentials;

/// <summary>Direct client for the freedesktop.org Secret Service on the user's D-Bus session.</summary>
internal static class LinuxSecretServiceCredentialStore
{
	private const string ServiceName = "org.freedesktop.secrets";
	private static readonly ObjectPath RootPath = new("/org/freedesktop/secrets");
	private static readonly ObjectPath NoPromptPath = new("/");

	/// <summary>
	/// One keyring operation at a time. Each operation opens and closes its own D-Bus connection,
	/// and gnome-keyring-daemon has been seen to abort (and restart locked, which is what raises
	/// the "login keyring did not get unlocked" prompt) when a client's connection vanishes while
	/// the daemon is still setting up its session. Serialising keeps those open/close cycles from
	/// overlapping each other.
	/// </summary>
	private static readonly SemaphoreSlim OneAtATime = new(1, 1);

	public static async Task<bool> IsAvailableAsync(CancellationToken ct)
	{
		try
		{
			using var connection = Connect(ct);
			await connection.ConnectAsync();
			var service = new DBusService(connection, ServiceName);
			return await service.CreateService(RootPath).ReadAliasAsync("default") != NoPromptPath;
		}
		catch (Exception) when (!ct.IsCancellationRequested)
		{
			return false;
		}
	}

	public static Task StoreAsync(string serviceName, Guid accountId, string encoded, CancellationToken ct) =>
		SerialisedAsync(() => StoreCoreAsync(serviceName, accountId, encoded, ct), ct);

	private static async Task StoreCoreAsync(string serviceName, Guid accountId, string encoded, CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var session = await service.CreateService(RootPath).OpenSessionAsync("plain", VariantValue.String(string.Empty));
		var collection = await service.CreateService(RootPath).ReadAliasAsync("default");
		if (collection == NoPromptPath) throw new InvalidOperationException("The Secret Service has no default collection.");
		await EnsureUnlockedAsync(service, [collection], userInitiated: false, ct);

		var item = await service.CreateCollection(collection).CreateItemAsync(
			new Dictionary<string, VariantValue>
			{
				["org.freedesktop.Secret.Item.Label"] = VariantValue.String(serviceName),
				["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(Attributes(serviceName, accountId)),
			},
			(session.Result, Array.Empty<byte>(), Encoding.UTF8.GetBytes(encoded), "text/plain"),
			replace: true
		);
		await CompletePromptAsync(connection, item.Prompt, ct);
	}

	/// <summary>
	/// Unlocks the default collection on the user's explicit request (the banner's unlock action),
	/// showing the desktop's own prompt even if an earlier one was declined recently.
	/// </summary>
	public static Task UnlockDefaultCollectionAsync(CancellationToken ct) =>
		SerialisedAsync(() => UnlockDefaultCollectionCoreAsync(ct), ct);

	private static async Task UnlockDefaultCollectionCoreAsync(CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var collection = await service.CreateService(RootPath).ReadAliasAsync("default");
		if (collection == NoPromptPath) throw new InvalidOperationException("The Secret Service has no default collection.");
		await EnsureUnlockedAsync(service, [collection], userInitiated: true, ct);
	}

	public static async Task<string?> RetrieveAsync(string serviceName, Guid accountId, CancellationToken ct)
	{
		string? result = null;
		await SerialisedAsync(async () => result = await RetrieveCoreAsync(serviceName, accountId, ct), ct);
		return result;
	}

	private static async Task<string?> RetrieveCoreAsync(string serviceName, Guid accountId, CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var item = await FindItemAsync(service, serviceName, accountId, ct);
		if (item is null) return null;

		// Opened only once there is something to read: a lookup that finds nothing needs no session.
		var session = await service.CreateService(RootPath).OpenSessionAsync("plain", VariantValue.String(string.Empty));
		var secret = await service.CreateItem(item.Value).GetSecretAsync(session.Result);
		return Encoding.UTF8.GetString(secret.Item3);
	}

	public static Task DeleteAsync(string serviceName, Guid accountId, CancellationToken ct) =>
		SerialisedAsync(() => DeleteCoreAsync(serviceName, accountId, ct), ct);

	private static async Task DeleteCoreAsync(string serviceName, Guid accountId, CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var item = await FindItemAsync(service, serviceName, accountId, ct);
		if (item is null) return;
		await CompletePromptAsync(connection, await service.CreateItem(item.Value).DeleteAsync(), ct);
	}

	private static async Task SerialisedAsync(Func<Task> operation, CancellationToken ct)
	{
		await OneAtATime.WaitAsync(ct);
		try
		{
			await operation();
		}
		finally
		{
			OneAtATime.Release();
		}
	}

	private static async Task<ObjectPath?> FindItemAsync(DBusService service, string serviceName, Guid accountId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var root = service.CreateService(RootPath);
		var result = await root.SearchItemsAsync(Attributes(serviceName, accountId));
		if (result.Unlocked.Length > 0) return result.Unlocked[0];
		if (result.Locked.Length == 0) return null;

		await EnsureUnlockedAsync(service, result.Locked, userInitiated: false, ct);
		result = await root.SearchItemsAsync(Attributes(serviceName, accountId));
		if (result.Unlocked.Length > 0) return result.Unlocked[0];

		// A matching item exists but stayed locked after an unlock that reported success — from
		// "no item found": returning null here would let RetrieveAsync report it as no
		// credential ever stored, when the truth is the store itself is inaccessible right now.
		throw new InvalidOperationException("The Secret Service left a matching item locked after unlocking.");
	}

	/// <summary>
	/// Unlocks <paramref name="objects"/>, showing the desktop's own unlock prompt when the
	/// service asks for one. Never locks anything: this client has no Lock call at all, so a
	/// keyring the user unlocked stays as they left it.
	/// </summary>
	private static async Task EnsureUnlockedAsync(DBusService service, ObjectPath[] objects, bool userInitiated, CancellationToken ct)
	{
		var unlocked = await SecretUnlockGate.Shared.RunAsync(
			async token =>
			{
				var unlock = await service.CreateService(RootPath).UnlockAsync(objects);
				if (unlock.Prompt == NoPromptPath) return true;
				return await PromptAsync(service.Connection, unlock.Prompt, token);
			},
			userInitiated,
			ct
		);
		if (!unlocked)
			throw new InvalidOperationException("The keyring is locked and its unlock prompt was declined or is not available.");
	}

	private static async Task CompletePromptAsync(DBusConnection connection, ObjectPath prompt, CancellationToken ct)
	{
		if (prompt == NoPromptPath) return;
		if (!await PromptAsync(connection, prompt, ct))
			throw new InvalidOperationException("The Secret Service prompt was dismissed.");
	}

	private static Task<bool> PromptAsync(DBusConnection connection, ObjectPath path, CancellationToken ct)
	{
		var prompt = new DBusService(connection, ServiceName).CreatePrompt(path);
		return SecretPrompt.RunAsync(
			async onCompleted => await prompt.WatchCompletedAsync(completed => onCompleted(completed.Dismissed)),
			// An empty window id: the service places the prompt itself.
			() => prompt.PromptAsync(string.Empty),
			SecretPrompt.DefaultTimeout,
			ct
		);
	}

	private static DBusConnection Connect(CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var address = DBusAddress.Session ?? throw new InvalidOperationException("No user D-Bus session is available.");
		return new DBusConnection(address);
	}

	private static Dictionary<string, string> Attributes(string serviceName, Guid accountId) =>
		new()
		{
			["service"] = serviceName,
			["account"] = accountId.ToString("N"),
		};
}
