using System.Security.Cryptography.X509Certificates;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Graph;
using MyloMail.Api.Security;

namespace MyloMail.Api.Providers;

/// <summary>
/// Resolves the calendar provider per account (§1, §2). Gmail and Microsoft 365 use their
/// native calendar APIs; CalDAV remains independently configured on an IMAP account.
/// </summary>
/// <remarks>
/// The <see cref="HttpClient"/> is constructed fresh per account rather than through
/// <c>IHttpClientFactory</c>'s pooling: certificate trust (§15) is a per-account decision —
/// <see cref="Account.CertificateTrustMode"/> and its pinned certificates — and a named,
/// DI-registered client's handler is configured once, shared by every account that resolves
/// this provider. A background sync poller's connection volume does not need pooled sockets
/// badly enough to trade away an honest per-account trust decision for it.
/// </remarks>
public sealed class CalendarProviderFactory(
	IOptions<ProviderClientOptions> options,
	ICredentialStore credentials,
	IMailProviderFactory mail,
	ITrustedCertificateStore certificates
) : ICalendarProviderFactory
{
	public ICalendarProvider For(Account account) => account.ProviderType switch
	{
		ProviderType.Gmail => Gmail(),
		ProviderType.Microsoft365 => Graph(),
		ProviderType.Imap => CalDav(account),
		_ => throw new ArgumentOutOfRangeException(nameof(account.ProviderType), account.ProviderType, null),
	};

	private GoogleCalendarProvider Gmail()
	{
		var gmail = options.Value.Gmail;
		if (!gmail.IsConfigured)
		{
			throw new ProviderNotConfiguredException(ProviderType.Gmail, "Google OAuth client registration");
		}

		return new GoogleCalendarProvider(
			new GmailOAuthAuthenticator(
				credentials,
				new ClientSecrets { ClientId = gmail.ClientId, ClientSecret = gmail.ClientSecret }
			)
		);
	}

	private GraphCalendarProvider Graph()
	{
		var graph = options.Value.Graph;
		if (!graph.IsConfigured)
		{
			throw new ProviderNotConfiguredException(ProviderType.Microsoft365, "Microsoft Graph client registration");
		}

		return new GraphCalendarProvider(new GraphOAuthAuthenticator(credentials, graph.ClientId!, graph.Authority));
	}

	private ICalendarProvider CalDav(Account account)
	{
		if (account.ProviderConfig is not ImapProviderConfig { CalDav: not null })
		{
			throw new ProviderNotConfiguredException(ProviderType.Imap, "CalDAV endpoint configuration");
		}

		var pinned = certificates.GetForAccount(account.Id);
		var handler = new HttpClientHandler();
		var rejectionHandler = new CertificateRejectionHandler(handler);
		handler.ServerCertificateCustomValidationCallback = (request, certificate, _, sslPolicyErrors) =>
		{
			if (certificate is null)
			{
				return false;
			}

			var hostname = request.RequestUri?.Host ?? string.Empty;
			var trusted = CertificateTrust.Validate(account.CertificateTrustMode, pinned, hostname, certificate, sslPolicyErrors);
			if (!trusted)
			{
				rejectionHandler.Rejected = (hostname, CertificateTrust.Fingerprint(certificate), certificate.Issuer);
			}
			return trusted;
		};

		return new CalDavCalendarProvider(new CalDavRequestFactory(credentials), new HttpClient(rejectionHandler), mail);
	}

	/// <summary>
	/// Every CalDAV request goes through <see cref="HttpClient"/>'s own error handling, which
	/// only ever surfaces a rejected certificate as a bare <see cref="HttpRequestException"/> —
	/// no fingerprint, hostname, or issuer, unlike IMAP's <c>AuthenticateAsync</c> and SMTP send
	/// path, which both translate the same rejection into <see cref="CertificateTrust.Problem"/>
	/// so the renderer can offer to pin it (§15). Wrapping the client, rather than each of
	/// <see cref="CalDavCalendarProvider"/>'s nine call sites individually, gives every CalDAV
	/// request the same translation from one place.
	/// </summary>
	internal sealed class CertificateRejectionHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
	{
		/// <summary>Set by the validation callback, synchronously, before the handshake it
		/// rejected can unwind into an exception here — the same ordering IMAP's own
		/// <c>rejectedCertificate</c> field relies on.</summary>
		public (string Hostname, string Fingerprint, string Issuer)? Rejected;

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			// Reset per attempt, exactly like ImapMailProvider.ConnectAsync's own
			// rejectedCertificate does: this handler is held and reused across every request
			// CalDavCalendarProvider makes over its lifetime, not recreated per call the way
			// IMAP gets a fresh ImapClient each time — without this, a rejection on one request
			// would stay stamped on the field forever, and a later, wholly unrelated transport
			// failure (a DNS blip, a dropped connection) would be mislabelled as that same
			// stale certificate problem.
			Rejected = null;
			try
			{
				return await base.SendAsync(request, ct);
			}
			catch (HttpRequestException) when (Rejected is { } rejected)
			{
				throw new ProviderAuthenticationException(
					CertificateTrust.Problem(rejected.Hostname, rejected.Fingerprint, rejected.Issuer).Detail!
				);
			}
		}
	}
}
