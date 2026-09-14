using Google.Apis.Auth.OAuth2;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Tests.Credentials;

namespace MyloMail.Api.Tests.Providers;

internal static class ProviderLiveTestContext
{
	private const string GmailCacheVariable = "GMAIL_TOKEN_CACHE_BASE64";
	private const string GraphCacheVariable = "GRAPH_TOKEN_CACHE_BASE64";
	private static readonly InMemoryCredentialStore GmailCredentials = new();
	private static readonly InMemoryCredentialStore GraphCredentials = new();
	private static readonly SemaphoreSlim GmailGate = new(1, 1);
	private static readonly SemaphoreSlim GraphGate = new(1, 1);

	public static string? GmailSkipReason =>
		string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID"))
		|| string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET"))
			? "GMAIL_CLIENT_ID/GMAIL_CLIENT_SECRET not set — source .dev/provider-test.env"
			: !ProviderTestCredentialCache.IsConfigured(GmailCacheVariable)
				? $"{GmailCacheVariable} not set — run pnpm provider:authorize gmail"
				: null;

	public static string? GraphSkipReason =>
		string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID"))
			? "GRAPH_CLIENT_ID not set — source .dev/provider-test.env"
			: !ProviderTestCredentialCache.IsConfigured(GraphCacheVariable)
				? $"{GraphCacheVariable} not set — run pnpm provider:authorize graph"
				: null;

	public static async Task<(Account Account, GmailOAuthAuthenticator OAuth)> GmailAsync()
	{
		await GmailGate.WaitAsync();
		try
		{
			var credential = ProviderTestCredentialCache.Load(
				GmailCacheVariable,
				"google-token-cache-v1"
			);
			await ProviderTestCredentialCache.SeedIfMissingAsync(
				GmailCredentials,
				credential,
				CancellationToken.None
			);
			var account = new Account
			{
				Id = credential.AccountId,
				DisplayName = "Gmail live provider",
				ProviderType = ProviderType.Gmail,
				AuthState = AuthState.Connected,
			};
			var oauth = new GmailOAuthAuthenticator(
				GmailCredentials,
				new ClientSecrets
				{
					ClientId = Environment.GetEnvironmentVariable("GMAIL_CLIENT_ID"),
					ClientSecret = Environment.GetEnvironmentVariable("GMAIL_CLIENT_SECRET"),
				}
			);
			return (account, oauth);
		}
		finally
		{
			GmailGate.Release();
		}
	}

	public static async Task<(Account Account, GraphOAuthAuthenticator OAuth)> GraphAsync()
	{
		await GraphGate.WaitAsync();
		try
		{
			var credential = ProviderTestCredentialCache.Load(
				GraphCacheVariable,
				"msal-token-cache-v1"
			);
			await ProviderTestCredentialCache.SeedIfMissingAsync(
				GraphCredentials,
				credential,
				CancellationToken.None
			);
			var account = new Account
			{
				Id = credential.AccountId,
				DisplayName = "Graph live provider",
				ProviderType = ProviderType.Microsoft365,
				AuthState = AuthState.Connected,
			};
			var tenant = Environment.GetEnvironmentVariable("GRAPH_TENANT_ID") ?? "common";
			var oauth = new GraphOAuthAuthenticator(
				GraphCredentials,
				Environment.GetEnvironmentVariable("GRAPH_CLIENT_ID")!,
				$"https://login.microsoftonline.com/{tenant}"
			);
			return (account, oauth);
		}
		finally
		{
			GraphGate.Release();
		}
	}
}
