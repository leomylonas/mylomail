using Google.Apis.Auth.OAuth2;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Credentials;

/// <summary>
/// Resolves either an account's bring-your-own installed-app registration or the deployment
/// registration. The client id is non-secret account configuration; the matching client secret
/// never leaves <see cref="ICredentialStore"/>.
/// </summary>
public static class GmailClientRegistration
{
	public const string SecretFormat = "google-oauth-client-secret";

	public static ClientSecrets Resolve(
		Account account,
		ProviderClientOptions options,
		ICredentialStore credentials
	)
	{
		if (account.ProviderConfig is GmailProviderConfig { ClientId.Length: > 0 } own)
		{
			var stored = credentials
				.RetrieveSlotAsync(account.Id, CredentialSlots.GmailClientSecret, CancellationToken.None)
				.GetAwaiter()
				.GetResult()
				?? throw new ProviderNotConfiguredException(
					ProviderType.Gmail,
					"the account's Google OAuth client secret"
				);
			if (stored.Format != SecretFormat || stored.Data.Length == 0)
			{
				throw new ProviderNotConfiguredException(
					ProviderType.Gmail,
					$"a '{SecretFormat}' credential for the account's Google OAuth client"
				);
			}

			return new ClientSecrets
			{
				ClientId = own.ClientId,
				ClientSecret = System.Text.Encoding.UTF8.GetString(stored.Data),
			};
		}

		if (!options.Gmail.IsConfigured)
		{
			throw new ProviderNotConfiguredException(
				ProviderType.Gmail,
				"Providers:Gmail:ClientId/ClientSecret or per-account Google OAuth credentials"
			);
		}

		return new ClientSecrets
		{
			ClientId = options.Gmail.ClientId,
			ClientSecret = options.Gmail.ClientSecret,
		};
	}
}
