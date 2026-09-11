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
	/// <summary>The credential-store format used for an IMAP account's password.</summary>
	public const string ImapPasswordFormat = "imap-password";
	public const string SmtpPasswordFormat = "smtp-basic-password";

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

		if (stored.Format != ImapPasswordFormat)
		{
			throw new ProviderNotConfiguredException(
				ProviderType.Imap,
				$"a '{ImapPasswordFormat}' credential (found '{stored.Format}')"
			);
		}

		var smtpStored = config.SmtpCredentialSource == CredentialSource.ReuseImap
			? stored
			: credentials.RetrieveSlotAsync(account.Id, CredentialSlots.Smtp, CancellationToken.None).GetAwaiter().GetResult()
				?? throw new ProviderNotConfiguredException(ProviderType.Imap, "a stored SMTP password");
		if (config.SmtpCredentialSource == CredentialSource.Independent && smtpStored.Format != SmtpPasswordFormat)
		{
			throw new ProviderNotConfiguredException(ProviderType.Imap, $"a '{SmtpPasswordFormat}' credential");
		}

		return new ImapMailProvider(
			new ImapConnectionSettings(
				config.Host,
				config.Port,
				config.UseSsl,
				config.UserName,
				System.Text.Encoding.UTF8.GetString(stored.Data),
				config.SmtpHost,
				config.SmtpPort,
				config.SmtpUserName,
				System.Text.Encoding.UTF8.GetString(smtpStored.Data),
				config.AppendToSentOnSend,
				account.CertificateTrustMode,
				certificates.GetForAccount(account.Id)
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
