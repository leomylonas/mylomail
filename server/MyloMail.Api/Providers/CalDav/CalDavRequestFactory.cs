using System.Net.Http.Headers;
using System.Text;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>
/// Creates CalDAV requests from one account's configured endpoint and credential selection.
/// </summary>
/// <remarks>
/// CalDAV is configured on a plain IMAP account but has its own transport identity. Reuse is
/// a lookup of the primary slot, never a copied password; independent Basic credentials stay
/// in the CalDAV slot and are resolved immediately before the WebDAV request (§4).
/// </remarks>
public sealed class CalDavRequestFactory(ICredentialStore credentials)
{
	public const string PasswordFormat = "caldav-basic-password";

	/// <summary>
	/// Builds a request against the account's configured collection, or against
	/// <paramref name="target"/> when the operation addresses one resource inside it (an
	/// event's own href, which the server assigns and is opaque to us).
	/// </summary>
	public async Task<HttpRequestMessage> CreateAsync(
		Account account,
		HttpMethod method,
		Uri? target = null,
		CancellationToken ct = default
	)
	{
		if (account.ProviderConfig is not ImapProviderConfig { CalDav: { } config })
		{
			throw new ProviderNotConfiguredException(ProviderType.Imap, "CalDAV endpoint configuration");
		}

		var endpoint = new Uri(config.Endpoint);
		var resolvedTarget = target ?? endpoint;
		if (!endpoint.IsBaseOf(resolvedTarget))
		{
			throw new InvalidOperationException("CalDAV resource target is outside the configured endpoint.");
		}

		var slot = config.CredentialSource == CredentialSource.ReuseImap
			? CredentialSlots.Primary
			: CredentialSlots.CalDav;
		var stored = slot == CredentialSlots.Primary
			? await credentials.RetrieveAsync(account.Id, ct)
			: await credentials.RetrieveSlotAsync(account.Id, slot, ct);
		var expectedFormat = slot == CredentialSlots.Primary ? MailProviderFactory.ImapPasswordFormat : PasswordFormat;
		if (stored is null || stored.Format != expectedFormat)
		{
			throw new ProviderNotConfiguredException(ProviderType.Imap, $"a '{expectedFormat}' CalDAV credential");
		}

		var request = new HttpRequestMessage(method, resolvedTarget);
		var raw = Encoding.UTF8.GetBytes($"{config.UserName}:{Encoding.UTF8.GetString(stored.Data)}");
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
		return request;
	}
}
