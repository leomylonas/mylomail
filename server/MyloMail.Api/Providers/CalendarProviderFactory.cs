using System.Security.Cryptography.X509Certificates;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Security;

namespace MyloMail.Api.Providers;

/// <summary>
/// Resolves the calendar provider per account (§1, §2). CalDAV is the only implementation:
/// it is configured independently on a plain IMAP account and never implied by having one.
/// Gmail and Microsoft 365 calendars (<c>GoogleCalendarProvider</c>, <c>GraphCalendarProvider</c>)
/// are a later slice, matching the mail providers' own staged build order.
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
	ICredentialStore credentials,
	IMailProviderFactory mail,
	ITrustedCertificateStore certificates
) : ICalendarProviderFactory
{
	public ICalendarProvider For(Account account)
	{
		if (account.ProviderType != ProviderType.Imap || account.ProviderConfig is not ImapProviderConfig { CalDav: not null })
		{
			throw new ProviderNotConfiguredException(account.ProviderType, "CalDAV endpoint configuration");
		}

		var pinned = certificates.GetForAccount(account.Id);
		var handler = new HttpClientHandler
		{
			ServerCertificateCustomValidationCallback = (request, certificate, _, sslPolicyErrors) =>
				certificate is not null
				&& CertificateTrust.Validate(
					account.CertificateTrustMode,
					pinned,
					request.RequestUri?.Host ?? string.Empty,
					certificate,
					sslPolicyErrors
				),
		};

		return new CalDavCalendarProvider(new CalDavRequestFactory(credentials), new HttpClient(handler), mail);
	}
}
