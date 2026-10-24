using Azure.Core;
using Microsoft.Identity.Client;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Graph;

namespace MyloMail.Api.Credentials;

/// <summary>
/// MSAL public-client authentication for Graph. Its cache is serialised into the application's
/// credential store; MSAL's default persistence is never enabled (§5).
/// </summary>
public sealed class GraphOAuthAuthenticator
{
	public static readonly IReadOnlyList<string> Scopes = ["Mail.ReadWrite", "Mail.Send", "Calendars.ReadWrite", "Contacts.ReadWrite"];

	/// <summary>
	/// Delegated scopes for a shared-mailbox account. Each <c>.Shared</c> scope covers the
	/// signed-in user's own mailbox as well as every mailbox delegated to them (Microsoft Graph
	/// permissions reference), so it replaces its plain counterpart rather than adding to it —
	/// the account never reads the user's personal data. Contacts are absent: a shared account
	/// has none. <c>.Shared</c> permissions are available to work or school accounts only.
	/// </summary>
	public static readonly IReadOnlyList<string> SharedMailboxScopes =
		["Mail.ReadWrite.Shared", "Mail.Send.Shared", "Calendars.ReadWrite.Shared"];

	public static IReadOnlyList<string> ScopesFor(Account account) =>
		GraphMailbox.For(account).IsShared ? SharedMailboxScopes : Scopes;

	/// <summary>
	/// The delegated permission for Graph's mailbox import and export API, which is the only way to
	/// store a received message in a mailbox as it was (<see cref="Providers.Graph.GraphMailProvider.ImportMessageAsync"/>).
	/// Work or school accounts only, and not part of <see cref="Scopes"/>: it is asked for the first
	/// time mail is moved into the account, so an account that is never a target is never asked.
	/// </summary>
	private static readonly SystemWebViewOptions ReturnPage = new()
	{
		HtmlMessageSuccess = OAuthReturnPage.Success,
		HtmlMessageError = OAuthReturnPage.Failure,
	};

	public const string ImportExportScope = "MailboxItem.ImportExport";

	public static IReadOnlyList<string> ImportScopesFor(Account account) =>
		[.. ScopesFor(account), ImportExportScope];

	/// <summary>How long the browser consent for the import permission is waited for.</summary>
	private static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(3);

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

	public Task<AuthResult> AuthenticateAsync(Account account, CancellationToken ct) =>
		AuthenticateCoreAsync(account, reauthenticate: false, ct);

	public Task<AuthResult> ReauthenticateAsync(Account account, CancellationToken ct) =>
		AuthenticateCoreAsync(account, reauthenticate: true, ct);

	private async Task<AuthResult> AuthenticateCoreAsync(
		Account account,
		bool reauthenticate,
		CancellationToken ct
	)
	{
		ConfigureCache(account.Id);

		try
		{
			if (!reauthenticate)
			{
				var cachedAccount = (await application.GetAccountsAsync()).SingleOrDefault();
				if (cachedAccount is not null)
				{
					try
					{
						await application.AcquireTokenSilent(ScopesFor(account), cachedAccount).ExecuteAsync(ct);
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
			}

			var interactive = application
				.AcquireTokenInteractive(ScopesFor(account))
				.WithUseEmbeddedWebView(false)
				.WithSystemWebViewOptions(ReturnPage);
			if (reauthenticate)
			{
				interactive = interactive.WithPrompt(Prompt.SelectAccount);
			}
			await interactive.ExecuteAsync(ct);
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

	/// <summary>
	/// Makes sure the account's sign-in covers the import permission: silently when it already does,
	/// otherwise through the browser consent screen, which is waited for a bounded time. Meant to be
	/// called from something the user just asked for, never from a background job.
	/// </summary>
	public async Task AuthorizeForImportAsync(Account account, CancellationToken ct)
	{
		ConfigureCache(account.Id);
		var scopes = ImportScopesFor(account);
		var cachedAccount = (await application.GetAccountsAsync()).SingleOrDefault();
		try
		{
			if (cachedAccount is not null)
			{
				try
				{
					await application.AcquireTokenSilent(scopes, cachedAccount).ExecuteAsync(ct);
					return;
				}
				catch (MsalUiRequiredException)
				{
					// Not consented yet, or the session needs renewing: ask.
				}
			}

			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
			timeout.CancelAfter(ConsentTimeout);
			var interactive = application.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithSystemWebViewOptions(ReturnPage);
			if (cachedAccount is not null)
			{
				interactive = interactive.WithAccount(cachedAccount);
			}

			await interactive.ExecuteAsync(timeout.Token);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new InvalidOperationException("The permission screen was not completed in time.");
		}
		catch (MsalException ex) when (IsAdminConsentRequired(ex))
		{
			throw new InvalidOperationException(
				"Your Microsoft 365 organisation needs an administrator to approve MyloMail's mailbox import permission (MailboxItem.ImportExport) before mail can be moved into this account."
			);
		}
		catch (MsalException ex)
		{
			throw new InvalidOperationException($"Microsoft did not grant the permission to move mail into this account: {ex.Message}");
		}
	}

	/// <summary>A token that carries the import permission; silent only, never a browser.</summary>
	public async Task<AccessToken> AcquireImportTokenAsync(Account account, CancellationToken ct)
	{
		ConfigureCache(account.Id);
		var cachedAccount = (await application.GetAccountsAsync()).SingleOrDefault()
			?? throw new MsalUiRequiredException("no_account", "No Microsoft credential is available for this account. Authenticate first.");
		var result = await application.AcquireTokenSilent(ImportScopesFor(account), cachedAccount).ExecuteAsync(ct);
		return new AccessToken(result.AccessToken, result.ExpiresOn);
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

		var result = await application.AcquireTokenSilent(ScopesFor(account), cachedAccount).ExecuteAsync(ct);
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
/// <param name="withImportPermission">Whether tokens carry the mailbox import permission as well.</param>
public sealed class GraphAccountTokenCredential(GraphOAuthAuthenticator oauth, Account account, bool withImportPermission = false)
	: TokenCredential
{
	public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct) =>
		GetTokenAsync(requestContext, ct).AsTask().GetAwaiter().GetResult();

	public override ValueTask<AccessToken> GetTokenAsync(
		TokenRequestContext requestContext,
		CancellationToken ct
	) => new(withImportPermission ? oauth.AcquireImportTokenAsync(account, ct) : oauth.AcquireTokenAsync(account, ct));
}
