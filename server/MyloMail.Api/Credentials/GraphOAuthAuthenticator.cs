using Azure.Core;
using Microsoft.Identity.Client;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Credentials;

/// <summary>
/// MSAL public-client authentication for Graph. Its cache is serialised into the application's
/// credential store; MSAL's default persistence is never enabled (§5).
/// </summary>
public sealed class GraphOAuthAuthenticator
{
	public static readonly IReadOnlyList<string> Scopes = ["Mail.ReadWrite", "Mail.Send"];

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
				await application.AcquireTokenSilent(Scopes, cachedAccount).ExecuteAsync(ct);
				return new AuthResult(true, AuthState.Connected, null);
			}

			await application
				.AcquireTokenInteractive(Scopes)
				.WithUseEmbeddedWebView(false)
				.ExecuteAsync(ct);
			return new AuthResult(true, AuthState.Connected, null);
		}
		catch (MsalException ex) when (IsAdminConsentRequired(ex))
		{
			// Not an authentication failure — the credentials are fine, and sending the
			// user to re-enter them would ask for something that was never the problem
			// (§5). Validation, not Auth: the central error mapping shows Detail verbatim
			// for Validation rather than the Auth category's re-authentication prompt,
			// which is exactly the wrong UX for "an administrator has to approve this app".
			var problem = new MutationProblemDetails
			{
				Title = "Administrator approval required",
				Detail =
					"This Microsoft 365 organisation has restricted user consent. Ask an "
					+ "administrator to approve MyloMail for your organisation, then try "
					+ "adding this account again.",
				Category = ErrorCategory.Validation,
			};
			// Distinguishes this from the generic Validation/ProviderRejected case in the
			// renderer's central mapping (§13), the same way a certificate rejection does via
			// its own extension fields — see CertificateTrust.Problem.
			problem.Extensions["adminConsentRequired"] = true;
			return new AuthResult(false, AuthState.Error, problem);
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
	/// A tenant blocking ordinary user consent surfaces either as MSAL's own
	/// <see cref="UiRequiredExceptionClassification.ConsentRequired"/> classification, or —
	/// for the interactive flow this method actually uses — as a raw Entra STS error code in
	/// a <see cref="MsalServiceException"/>: <c>AADSTS65001</c> (user consent required) or
	/// <c>AADSTS90094</c> (admin consent required for this specific app), neither of which
	/// MSAL itself further classifies (§5).
	/// </summary>
	private static bool IsAdminConsentRequired(MsalException ex) =>
		ex is MsalUiRequiredException { Classification: UiRequiredExceptionClassification.ConsentRequired }
		|| (ex is MsalServiceException { Message: var message }
			&& (message.Contains("AADSTS65001", StringComparison.Ordinal)
				|| message.Contains("AADSTS90094", StringComparison.Ordinal)));

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
