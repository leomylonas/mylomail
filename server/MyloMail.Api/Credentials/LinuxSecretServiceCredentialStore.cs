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

	public static async Task StoreAsync(string serviceName, Guid accountId, string encoded, CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var session = await service.CreateService(RootPath).OpenSessionAsync(VariantValue.String("plain"));
		var collection = await service.CreateService(RootPath).ReadAliasAsync("default");
		if (collection == NoPromptPath) throw new InvalidOperationException("The Secret Service has no default collection.");

		var item = await service.CreateCollection(collection).CreateItemAsync(
			new Dictionary<string, VariantValue>
			{
				["org.freedesktop.Secret.Item.Label"] = VariantValue.String(serviceName),
				["org.freedesktop.Secret.Item.Attributes"] = new Dict<string, string>(Attributes(serviceName, accountId)),
			},
			(session.Result, Array.Empty<byte>(), Encoding.UTF8.GetBytes(encoded), "text/plain"),
			replace: true
		);
		EnsureNoPrompt(item.Prompt);
	}

	public static async Task<string?> RetrieveAsync(string serviceName, Guid accountId, CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var session = await service.CreateService(RootPath).OpenSessionAsync(VariantValue.String("plain"));
		var item = await FindItemAsync(service, serviceName, accountId, ct);
		if (item is null) return null;

		var secret = await service.CreateItem(item.Value).GetSecretAsync(session.Result);
		return Encoding.UTF8.GetString(secret.Item3);
	}

	public static async Task DeleteAsync(string serviceName, Guid accountId, CancellationToken ct)
	{
		using var connection = Connect(ct);
		await connection.ConnectAsync();
		var service = new DBusService(connection, ServiceName);
		var item = await FindItemAsync(service, serviceName, accountId, ct);
		if (item is null) return;
		EnsureNoPrompt(await service.CreateItem(item.Value).DeleteAsync());
	}

	private static async Task<ObjectPath?> FindItemAsync(DBusService service, string serviceName, Guid accountId, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var result = await service.CreateService(RootPath).SearchItemsAsync(Attributes(serviceName, accountId));
		if (result.Unlocked.Length > 0) return result.Unlocked[0];
		if (result.Locked.Length == 0) return null;

		var unlock = await service.CreateService(RootPath).UnlockAsync(result.Locked);
		EnsureNoPrompt(unlock.Prompt);
		return unlock.Unlocked.Length > 0 ? unlock.Unlocked[0] : (ObjectPath?)null;
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

	private static void EnsureNoPrompt(ObjectPath prompt)
	{
		if (prompt != NoPromptPath) throw new InvalidOperationException("The Secret Service requires an interactive unlock prompt.");
	}
}
