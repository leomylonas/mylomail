using Azure.Core;
using Microsoft.Identity.Client;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Credentials;

/// <summary>
/// MSAL public-client authentication for Graph. Its cache is serialised into the application's
/// credential store; MSAL's default persistence is never enabled (§5).
/// </summary>
public sealed class GraphOAuthAuthenticator
{
	public static readonly IReadOnlyList<string> Scopes = ["Mail.ReadWrite", "Mail.Send", "Calendars.ReadWrite", "Contacts.ReadWrite"];

	private readonly ICredentialStore credentials;
	private readonly IPublicClientApplication application;

	public GraphOAuthAuthenticator(ICredentialStore credentials, string clientId, string authority)
	{
		this.credentials = credentials;
		application = PublicClientApplicationBuilder
			.Create(clientId)
			.WithAuthority(authority)
			.WithRedirectUri("http://localhost")
			.Build();
	}

	public async Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct)
	{
		ConfigureCache(account.Id);

		try
		{
			var cachedAccount = (await application.GetAccountsAsync()).SingleOrDefault();
			if (cachedAccount is not null)
			{
				try
				{
					await application.AcquireTokenSilent(Scopes, cachedAccount).ExecuteAsync(ct);
					return new AuthResult(true, AuthState.Connected, null);
				}
				catch (MsalUiRequiredException)
				{
					// MSAL uses this for every silent-cache state the user can recover by
					// signing in again (expired/revoked refresh tokens, password changes and
					// incremental consent). The interactive request below remains responsible
					// for distinguishing an actual administrator-consent policy denial.
				}
			}

			await application
				.AcquireTokenInteractive(Scopes)
				.WithUseEmbeddedWebView(false)
				.ExecuteAsync(ct);
			return new AuthResult(true, AuthState.Connected, null);
		}
		catch (MsalException ex) when (IsAdminConsentRequired(ex))
		{
			return new AuthResult(false, AuthState.Error, AdminConsentProblem());
		}
		catch (MsalException ex)
		{
			return Failure(ErrorCategory.Auth, "Microsoft authentication failed", ex.Message);
		}
		catch (IOException ex)
		{
			return Failure(ErrorCategory.Network, "Could not reach Microsoft", ex.Message);
		}
	}

	/// <summary>
	/// A tenant blocking ordinary user consent surfaces as the interactive STS error
	/// <c>AADSTS90094</c>. Every silent <see cref="MsalUiRequiredException"/> is instead
	/// interactive recovery for the cached session; it must not be reported as administrator
	/// denial (§5).
	/// </summary>
	internal static bool IsAdminConsentRequired(MsalException ex) =>
		ex is MsalServiceException { Message: var message }
			&& message.Contains("AADSTS90094", StringComparison.Ordinal);

	internal static ProviderAuthenticationException TranslateTokenFailure(MsalException ex) =>
		IsAdminConsentRequired(ex)
			? new ProviderAuthenticationException(AdminConsentProblem(), ex)
			: new ProviderAuthenticationException(ex.Message, ex);

	private static MutationProblemDetails AdminConsentProblem()
	{
		var problem = new MutationProblemDetails
		{
			Title = "Administrator approval required",
			Detail =
				"This Microsoft 365 organisation has restricted user consent. Ask an "
				+ "administrator to approve MyloMail for your organisation, then try again.",
			Category = ErrorCategory.Validation,
		};
		problem.Extensions["adminConsentRequired"] = true;
		return problem;
	}

	public async Task<AccessToken> AcquireTokenAsync(Account account, CancellationToken ct)
	{
		ConfigureCache(account.Id);
		var cachedAccount = (await application.GetAccountsAsync()).SingleOrDefault();
		if (cachedAccount is null)
		{
			throw new MsalUiRequiredException(
				"no_account",
				"No Microsoft credential is available for this account. Authenticate first."
			);
		}

		var result = await application.AcquireTokenSilent(Scopes, cachedAccount).ExecuteAsync(ct);
		return new AccessToken(result.AccessToken, result.ExpiresOn);
	}

	private void ConfigureCache(Guid accountId)
	{
		application.UserTokenCache.SetBeforeAccessAsync(async args =>
		{
			var payload = await credentials.RetrieveAsync(accountId, args.CancellationToken);
			if (payload is null)
			{
				return;
			}

			if (payload.Format != "msal-token-cache-v1")
			{
				throw new InvalidOperationException(
					$"Credential payload for account '{accountId}' belongs to '{payload.Format}', not MSAL."
				);
			}

			args.TokenCache.DeserializeMsalV3(payload.Data);
		});

		application.UserTokenCache.SetAfterAccessAsync(async args =>
		{
			if (args.HasStateChanged)
			{
				await credentials.StoreAsync(
					accountId,
					new CredentialPayload("msal-token-cache-v1", args.TokenCache.SerializeMsalV3()),
					args.CancellationToken
				);
			}
		});
	}

	private static AuthResult Failure(ErrorCategory category, string title, string detail) =>
		new(
			false,
			category == ErrorCategory.Auth ? AuthState.NeedsReauth : AuthState.Error,
			new MutationProblemDetails { Title = title, Detail = detail, Category = category }
		);

}

/// <summary>Bridges MSAL's per-account token cache into the Graph SDK's token credential.</summary>
public sealed class GraphAccountTokenCredential(GraphOAuthAuthenticator oauth, Account account)
	: TokenCredential
{
	public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) =>
		GetTokenAsync(requestContext, ct).AsTask().GetAwaiter().GetResult();

	public override ValueTask<AccessToken> GetTokenAsync(
		TokenRequestContext requestContext,
		CancellationToken ct
	) => new(oauth.AcquireTokenAsync(account, ct));
}
