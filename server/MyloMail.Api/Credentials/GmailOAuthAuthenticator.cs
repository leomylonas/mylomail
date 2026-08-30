using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Credentials;

/// <summary>
/// Google's installed-app loopback OAuth flow. Credentials are retained exclusively through
/// <see cref="GoogleCredentialDataStore"/> rather than Google's default local file store.
/// </summary>
public sealed class GmailOAuthAuthenticator(ICredentialStore credentials, ClientSecrets client)
{
	public static readonly IReadOnlyList<string> Scopes =
	[
		"https://www.googleapis.com/auth/gmail.modify",
	];

	public async Task<UserCredential> AuthorizeAsync(Account account, CancellationToken ct)
	{
		var flow = new GoogleAuthorizationCodeFlow.Initializer
		{
			ClientSecrets = client,
			DataStore = new GoogleCredentialDataStore(credentials, account.Id),
			Scopes = Scopes,
		};

		return await GoogleWebAuthorizationBroker.AuthorizeAsync(
			flow,
			Scopes,
			account.Id.ToString("N"),
			usePkce: true,
			ct,
			flow.DataStore,
			new DiagnosticsLocalServerCodeReceiver()
		);
	}

	public async Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct)
	{
		try
		{
			await AuthorizeAsync(account, ct);
			return new AuthResult(true, AuthState.Connected, null);
		}
		catch (TokenResponseException ex)
		{
			return Failure(ErrorCategory.Auth, "Google authentication failed", ex.Message);
		}
		catch (IOException ex)
		{
			return Failure(ErrorCategory.Network, "Could not reach Google", ex.Message);
		}
	}

	private static AuthResult Failure(ErrorCategory category, string title, string detail) =>
		new(
			false,
			category == ErrorCategory.Auth ? AuthState.NeedsReauth : AuthState.Error,
			new MutationProblemDetails { Title = title, Detail = detail, Category = category }
		);

}
