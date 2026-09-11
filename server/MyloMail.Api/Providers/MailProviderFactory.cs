using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Graph;
using MyloMail.Api.Providers.Imap;
using MyloMail.Api.Security;

namespace MyloMail.Api.Providers;

/// <summary>
/// Constructs the provider for an account, so the orchestrator and hub layers contain no
/// provider-specific logic (§2).
/// </summary>
/// <remarks>
/// <b>Resolution is per account, not per provider type.</b> IMAP has no account-independent
/// implementation: host, port, user and password differ per account and the password is
/// resolved from the credential store at the moment of use. Gmail and Graph ignore the
/// account here only because their client registration is application-wide.
/// </remarks>
public sealed class MailProviderFactory(
	IOptions<ProviderClientOptions> options,
	ICredentialStore credentials,
	IProviderMailboxResolver mailboxes,
	ITrustedCertificateStore certificates
) : IMailProviderFactory
{
	/// <summary>Credential-store formats distinguish passwords from OAuth bearer tokens.</summary>
	public const string ImapPasswordFormat = "imap-password";
	public const string ImapOAuth2TokenFormat = "imap-oauth2-token";
	public const string SmtpPasswordFormat = "smtp-basic-password";
	public const string SmtpOAuth2TokenFormat = "smtp-oauth2-token";

	public IMailProvider For(Account account) =>
		account.ProviderType switch
		{
			ProviderType.Gmail => Gmail(account),
			ProviderType.Microsoft365 => Graph(),
			ProviderType.Imap => Imap(account),
			_ => throw new ArgumentOutOfRangeException(
				nameof(account),
				account.ProviderType,
				"Unknown provider type."
			),
		};

	private GmailMailProvider Gmail(Account account) =>
		new(
			new GmailOAuthAuthenticator(
				credentials,
				GmailClientRegistration.Resolve(account, options.Value, credentials)
			),
			mailboxes
		);

	private GraphMailProvider Graph()
	{
		var graph = options.Value.Graph;
		if (!graph.IsConfigured)
		{
			throw new ProviderNotConfiguredException(ProviderType.Microsoft365, "Providers:Graph:ClientId");
		}

		return new GraphMailProvider(new GraphOAuthAuthenticator(credentials, graph.ClientId!, graph.Authority));
	}

	/// <summary>
	/// Builds an IMAP provider from the account's non-secret configuration plus the password
	/// held in the credential store.
	/// </summary>
	/// <remarks>
	/// The password is read here, at the moment of use, and never stored on the
	/// <see cref="Account"/> — §4's boundary. A missing one is reported as unconfigured rather
	/// than attempted with an empty string, which would look to the server like a failed login
	/// and could count against the account's attempt limit.
	/// </remarks>
	private ImapMailProvider Imap(Account account)
	{
		if (account.ProviderConfig is not ImapProviderConfig config)
		{
			throw new ProviderNotConfiguredException(ProviderType.Imap, $"{nameof(ImapProviderConfig)} on the account");
		}

		var stored =
			credentials.RetrieveAsync(account.Id, CancellationToken.None).GetAwaiter().GetResult()
			?? throw new ProviderNotConfiguredException(ProviderType.Imap, "a stored password");

		var imapFormat = config.AuthMethod == ImapAuthMethod.OAuth2
			? ImapOAuth2TokenFormat
			: ImapPasswordFormat;
		if (stored.Format != imapFormat)
		{
			throw new ProviderNotConfiguredException(
				ProviderType.Imap,
				$"a '{imapFormat}' credential (found '{stored.Format}')"
			);
		}

		CredentialPayload? smtpStored = null;
		if (config.SmtpAuthMethod != SmtpAuthMethod.None)
		{
			smtpStored = config.SmtpCredentialSource == CredentialSource.ReuseImap
				? stored
				: credentials
					.RetrieveSlotAsync(account.Id, CredentialSlots.Smtp, CancellationToken.None)
					.GetAwaiter()
					.GetResult()
					?? throw new ProviderNotConfiguredException(ProviderType.Imap, "a stored SMTP credential");
			if (config.SmtpCredentialSource == CredentialSource.Independent)
			{
				var smtpFormat = config.SmtpAuthMethod == SmtpAuthMethod.OAuth2
					? SmtpOAuth2TokenFormat
					: SmtpPasswordFormat;
				if (smtpStored.Format != smtpFormat)
				{
					throw new ProviderNotConfiguredException(ProviderType.Imap, $"a '{smtpFormat}' credential");
				}
			}
		}

		return new ImapMailProvider(
			new ImapConnectionSettings(
				Host: config.Host,
				Port: config.Port,
				ImapSecurity: config.ImapSecurity,
				UserName: config.UserName,
				Password: System.Text.Encoding.UTF8.GetString(stored.Data),
				AuthMethod: config.AuthMethod,
				SmtpHost: config.SmtpHost,
				SmtpPort: config.SmtpPort,
				SmtpSecurity: config.SmtpSecurity,
				SmtpAuthMethod: config.SmtpAuthMethod,
				SmtpUserName: config.SmtpUserName,
				SmtpPassword: smtpStored is null
					? null
					: System.Text.Encoding.UTF8.GetString(smtpStored.Data),
				AppendToSent: config.AppendToSentOnSend,
				CertificateTrustMode: account.CertificateTrustMode,
				TrustedCertificates: certificates.GetForAccount(account.Id)
			),
			mailboxes
		);
	}
}

/// <summary>
/// A provider cannot be built because this deployment has not supplied what it needs.
/// </summary>
/// <remarks>
/// Distinct from an authentication failure: nothing was attempted, so the account must not be
/// marked <see cref="AuthState.NeedsReauth"/> — reauthenticating cannot supply a client id.
/// </remarks>
public sealed class ProviderNotConfiguredException(ProviderType provider, string missing)
	: Exception($"The {provider} provider is not configured: {missing} is missing.")
{
	public ProviderType Provider { get; } = provider;
}
