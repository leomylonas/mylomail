using System.Collections.Concurrent;
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
	private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ScopeUpgradeLocks = [];
	public static readonly IReadOnlyList<string> Scopes =
	[
		"https://www.googleapis.com/auth/gmail.modify",
		"https://www.googleapis.com/auth/calendar",
	];
	public Task<UserCredential> AuthorizeAsync(Account account, CancellationToken ct) =>
		AuthorizeAsync(account, requireCalendarScope: false, ct);

	public async Task<UserCredential> AuthorizeAsync(
		Account account,
		bool requireCalendarScope,
		CancellationToken ct
	)
	{
		var gate = ScopeUpgradeLocks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct);
		try
		{
			return await AuthorizeUnlockedAsync(account, requireCalendarScope, ct);
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<UserCredential> AuthorizeUnlockedAsync(
		Account account,
		bool requireCalendarScope,
		CancellationToken ct
	)
	{
		var flow = new GoogleAuthorizationCodeFlow.Initializer
		{
			ClientSecrets = client,
			DataStore = new GoogleCredentialDataStore(credentials, account.Id),
			Scopes = Scopes,
		};

		var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
			flow,
			Scopes,
			account.Id.ToString("N"),
			usePkce: true,
			ct,
			flow.DataStore,
			new DiagnosticsLocalServerCodeReceiver()
		);
		var requiredScope = requireCalendarScope
			? "https://www.googleapis.com/auth/calendar"
			: "https://www.googleapis.com/auth/gmail.modify";
		if (string.IsNullOrWhiteSpace(credential.Token.Scope)
			|| !credential.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Contains(requiredScope, StringComparer.Ordinal))
		{
			var gate = ScopeUpgradeLocks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync(ct);
			try
			{
				var previous = await credentials.RetrieveAsync(account.Id, ct);
				await flow.DataStore.ClearAsync();
				try
				{
					var refreshed = await GoogleWebAuthorizationBroker.AuthorizeAsync(
						flow,
						Scopes,
						account.Id.ToString("N"),
						usePkce: true,
						ct,
						flow.DataStore,
						new DiagnosticsLocalServerCodeReceiver()
					);
					if (string.IsNullOrWhiteSpace(refreshed.Token.Scope)
						|| !refreshed.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
							.Contains(requiredScope, StringComparer.Ordinal))
					{
						throw new TokenResponseException(
							new TokenErrorResponse { Error = "insufficient_scope", ErrorDescription = $"Google did not grant {requiredScope}." }
						);
					}
					return refreshed;
				}
				catch
				{
					if (previous is not null)
					{
						await credentials.StoreAsync(account.Id, previous, CancellationToken.None);
					}
					throw;
				}
			}
			finally
			{
				gate.Release();
			}
		}
		return credential;
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
