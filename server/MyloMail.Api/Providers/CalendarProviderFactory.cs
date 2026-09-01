using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.CalDav;

namespace MyloMail.Api.Providers;

/// <summary>
/// Resolves the calendar provider per account (§1, §2). CalDAV is the only implementation:
/// it is configured independently on a plain IMAP account and never implied by having one.
/// Gmail and Microsoft 365 calendars (<c>GoogleCalendarProvider</c>, <c>GraphCalendarProvider</c>)
/// are a later slice, matching the mail providers' own staged build order.
/// </summary>
public sealed class CalendarProviderFactory(ICredentialStore credentials, IHttpClientFactory httpClients)
	: ICalendarProviderFactory
{
	public ICalendarProvider For(Account account)
	{
		if (account.ProviderType != ProviderType.Imap || account.ProviderConfig is not ImapProviderConfig { CalDav: not null })
		{
			throw new ProviderNotConfiguredException(account.ProviderType, "CalDAV endpoint configuration");
		}

		return new CalDavCalendarProvider(new CalDavRequestFactory(credentials), httpClients.CreateClient(nameof(CalDavCalendarProvider)));
	}
}
