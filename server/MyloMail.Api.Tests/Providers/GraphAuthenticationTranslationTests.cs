using Microsoft.Identity.Client;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Graph;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// Unlike Gmail's <c>GmailOAuthAuthenticator.AuthorizeAsync</c> (which needs a real network round
/// trip to fail in a useful way), <c>GraphOAuthAuthenticator.AcquireTokenAsync</c> throws
/// <c>MsalUiRequiredException("no_account", ...)</c> deterministically, with no network call at
/// all, whenever MSAL has no cached account for the id — exactly the state of a freshly
/// constructed authenticator backed by an empty credential store. That makes a genuine, offline
/// discriminator possible here, unlike the SMTP case in pass 197.
/// </summary>
public sealed class GraphAuthenticationTranslationTests
{
	[Fact]
	public async Task A_missing_cached_account_surfaces_as_provider_authentication_not_a_raw_msal_exception()
	{
		var oauth = new GraphOAuthAuthenticator(
			new InMemoryCredentialStore(),
			Guid.NewGuid().ToString(),
			"https://login.microsoftonline.com/common"
		);
		var provider = new GraphMailProvider(oauth);
		var account = new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Microsoft365 };

		await Assert.ThrowsAsync<ProviderAuthenticationException>(
			() => provider.SyncMailboxTopologyAsync(account, null, CancellationToken.None)
		);
	}

	[Fact]
	public void Administrator_consent_failure_preserves_structured_validation_state()
	{
		var failure = GraphOAuthAuthenticator.TranslateTokenFailure(
			new MsalServiceException("code", "AADSTS90094: admin consent required")
		);

		Assert.Equal(AuthState.Error, failure.AccountState);
		Assert.Equal(ErrorCategory.Validation, failure.Problem?.Category);
		Assert.Equal(true, failure.Problem?.Extensions["adminConsentRequired"]);
	}

	[Fact]
	public void Ordinary_silent_token_failure_requests_reauthentication()
	{
		var failure = GraphOAuthAuthenticator.TranslateTokenFailure(
			new MsalUiRequiredException("no_account", "authenticate again")
		);

		Assert.Equal(AuthState.NeedsReauth, failure.AccountState);
		Assert.Null(failure.Problem);
	}
	[Theory]
	[InlineData(UiRequiredExceptionClassification.ConsentRequired, "unrelated", false)]
	[InlineData(UiRequiredExceptionClassification.None, "unrelated", false)]
	public void Admin_consent_required_is_recognised_from_the_ui_required_classification(
		UiRequiredExceptionClassification classification,
		string message,
		bool expected
	)
	{
		var ex = new MsalUiRequiredException("code", message, null, classification);

		Assert.Equal(expected, GraphOAuthAuthenticator.IsAdminConsentRequired(ex));
	}

	[Theory]
	[InlineData("AADSTS65001: user consent required", false)]
	[InlineData("AADSTS90094: admin consent required", true)]
	[InlineData("AADSTS50126: invalid credentials", false)]
	public void Admin_consent_required_is_recognised_from_the_service_exception_message(
		string message,
		bool expected
	)
	{
		var ex = new MsalServiceException("code", message);

		Assert.Equal(expected, GraphOAuthAuthenticator.IsAdminConsentRequired(ex));
	}
}
