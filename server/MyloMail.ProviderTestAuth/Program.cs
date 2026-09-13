using System.Diagnostics;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;

if (args.Length != 2 || args[0] is not ("gmail" or "graph"))
{
	Console.Error.WriteLine("Usage: provider:authorize <gmail|graph> <provider-env-file>");
	return 2;
}

var provider = args[0];
var environmentPath = Path.GetFullPath(args[1]);
var saved = provider switch
{
	"gmail" => await AuthorizeGmailAsync(),
	"graph" => await AuthorizeGraphAsync(),
	_ => throw new UnreachableException(),
};

UpsertEnvironmentVariable(environmentPath, saved.Variable, Encode(saved));
Console.WriteLine($"Saved {saved.Variable} in {Path.GetRelativePath(Environment.CurrentDirectory, environmentPath)}.");
return 0;

static async Task<SavedCredential> AuthorizeGmailAsync()
{
	var accountId = Guid.NewGuid();
	var store = new InMemoryCredentialStore();
	var authenticator = new GmailOAuthAuthenticator(
		store,
		new ClientSecrets
		{
			ClientId = RequiredEnvironmentVariable("GMAIL_CLIENT_ID"),
			ClientSecret = RequiredEnvironmentVariable("GMAIL_CLIENT_SECRET"),
		}
	);
	var account = new Account { Id = accountId, ProviderType = ProviderType.Gmail };
	Console.WriteLine("Opening Gmail authorization in the system browser.");

	await authenticator.AuthorizeAsync(
		account,
		requireCalendarScope: true,
		requireContactsScope: false,
		CancellationToken.None
	);
	await authenticator.AuthorizeAsync(
		account,
		requireCalendarScope: false,
		requireContactsScope: true,
		CancellationToken.None
	);

	return await CaptureAsync("GMAIL_TOKEN_CACHE_BASE64", accountId, store);
}

static async Task<SavedCredential> AuthorizeGraphAsync()
{
	var accountId = Guid.NewGuid();
	var store = new InMemoryCredentialStore();
	var tenant = RequiredEnvironmentVariable("GRAPH_TENANT_ID");
	var authenticator = new GraphOAuthAuthenticator(
		store,
		RequiredEnvironmentVariable("GRAPH_CLIENT_ID"),
		$"https://login.microsoftonline.com/{tenant}"
	);
	var account = new Account
	{
		Id = accountId,
		DisplayName = "Graph provider test",
		ProviderType = ProviderType.Microsoft365,
	};
	Console.WriteLine("Opening Microsoft authorization in the system browser.");
	var result = await authenticator.AuthenticateAsync(account, CancellationToken.None);
	if (!result.Succeeded)
	{
		throw new InvalidOperationException(result.Problem?.Detail ?? "Microsoft authentication failed.");
	}

	return await CaptureAsync("GRAPH_TOKEN_CACHE_BASE64", accountId, store);
}

static async Task<SavedCredential> CaptureAsync(
	string variable,
	Guid accountId,
	ICredentialStore store
)
{
	var payload = await store.RetrieveAsync(accountId, CancellationToken.None)
		?? throw new InvalidOperationException("The provider completed authorization without saving a token cache.");
	return new SavedCredential(
		variable,
		accountId,
		payload.Format,
		Convert.ToBase64String(payload.Data)
	);
}

static string Encode(SavedCredential saved) =>
	Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(saved));

static string RequiredEnvironmentVariable(string name) =>
	Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
		? value
		: throw new InvalidOperationException($"{name} is missing from .dev/provider-test.env.");

static void UpsertEnvironmentVariable(string path, string name, string value)
{
	var directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
	Directory.CreateDirectory(directory);
	var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
	var replacement = $"export {name}='{value}'";
	var replaced = false;

	for (var index = 0; index < lines.Count; index++)
	{
		var candidate = lines[index].TrimStart();
		if (candidate.StartsWith("export ", StringComparison.Ordinal))
		{
			candidate = candidate["export ".Length..].TrimStart();
		}
		var separator = candidate.IndexOf('=');
		if (separator < 0 || !candidate[..separator].Trim().Equals(name, StringComparison.Ordinal))
		{
			continue;
		}

		lines[index] = replacement;
		replaced = true;
		break;
	}

	if (!replaced)
	{
		lines.Add(replacement);
	}

	var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
	try
	{
		File.WriteAllText(temporaryPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(
				temporaryPath,
				UnixFileMode.UserRead | UnixFileMode.UserWrite
			);
		}
		File.Move(temporaryPath, path, overwrite: true);
	}
	finally
	{
		File.Delete(temporaryPath);
	}
}

internal sealed record SavedCredential(
	string Variable,
	Guid AccountId,
	string Format,
	string Data
);
