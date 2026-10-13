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
	/// <summary>
	/// Google's full-access Gmail scope. <c>gmail.modify</c> cannot delete permanently, so this is
	/// the only scope under which "Delete" and "Empty Trash" can work. It is requested from new
	/// accounts at sign-in, and from an existing account only when it first deletes permanently.
	/// </summary>
	public const string FullAccessScope = "https://mail.google.com/";

	public static readonly IReadOnlyList<string> Scopes =
	[
		"https://www.googleapis.com/auth/gmail.modify",
		FullAccessScope,
		"https://www.googleapis.com/auth/calendar",
		"https://www.googleapis.com/auth/contacts",
	];
	public Task<UserCredential> AuthorizeAsync(Account account, CancellationToken ct) =>
		AuthorizeAsync(account, requireCalendarScope: false, requireContactsScope: false, ct);

	/// <summary>
	/// As <see cref="AuthorizeAsync(Account, CancellationToken)"/>, but the credential must carry the
	/// full-access scope. An account that signed in without it is taken through Google's consent
	/// screen again, once; declining surfaces as <see cref="TokenResponseException"/> with
	/// <c>insufficient_scope</c>.
	/// </summary>
	public async Task<UserCredential> AuthorizeForPermanentDeleteAsync(Account account, CancellationToken ct)
	{
		var gate = ScopeUpgradeLocks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct);
		try
		{
			return await AuthorizeUnlockedAsync(account, FullAccessScope, ct);
		}
		finally
		{
			gate.Release();
		}
	}

	public async Task<UserCredential> AuthorizeAsync(
		Account account,
		bool requireCalendarScope,
		bool requireContactsScope,
		CancellationToken ct
	)
	{
		var gate = ScopeUpgradeLocks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct);
		try
		{
			return await AuthorizeUnlockedAsync(
				account,
				requireCalendarScope
					? "https://www.googleapis.com/auth/calendar"
					: requireContactsScope
						? "https://www.googleapis.com/auth/contacts"
						: "https://www.googleapis.com/auth/gmail.modify",
				ct
			);
		}
		finally
		{
			gate.Release();
		}
	}

	private async Task<UserCredential> AuthorizeUnlockedAsync(
		Account account,
		string requiredScope,
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
		if (string.IsNullOrWhiteSpace(credential.Token.Scope)
			|| !credential.Token.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
				.Contains(requiredScope, StringComparer.Ordinal))
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
			var detail = ex.Error?.Error switch
			{
				"invalid_client" or "unauthorized_client" =>
					"Google rejected this OAuth registration. Use a Desktop app client ID and its matching secret.",
				"invalid_grant" =>
					"Google rejected or expired the authorization. Reauthorize the account; if the OAuth consent screen is in Testing, its refresh tokens expire after about seven days.",
				"insufficient_scope" =>
					"Google did not grant every required permission. Reauthorize and allow Gmail, Calendar, and Contacts access.",
				_ => ex.Message,
			};
			return Failure(ErrorCategory.Auth, "Google authentication failed", detail);
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
