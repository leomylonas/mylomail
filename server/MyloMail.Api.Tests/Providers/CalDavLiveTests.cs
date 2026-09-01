using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

/// <summary>
/// Full CalDAV event CRUD against a real HTTPS server (§13 Calendar).
/// </summary>
/// <remarks>
/// <para>
/// Closes the verification gap the Epic 7 UI work left open: the backend requires HTTPS for
/// CalDAV Basic auth (904908f), so only this — a real server, not the fake transport
/// <see cref="CalDavCalendarProviderTests"/> uses — can prove the request/response shapes this
/// provider builds are ones a real CalDAV server actually accepts.
/// </para>
/// <para>
/// The test's own <see cref="HttpClient"/> accepts any server certificate. That is specific to
/// this one client, constructed directly here rather than through the app's DI-registered
/// <c>AddHttpClient(nameof(CalDavCalendarProvider))</c> pipeline — nothing in the production
/// trust path is touched. Certificate pinning (<c>AccountTrustedCertificate</c>,
/// <c>Account.CertificateTrustMode</c>) is designed in §1/§9 but not yet wired to any transport
/// for any provider; faithfully testing that is a separate, not-yet-built feature.
/// </para>
/// </remarks>
[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
public sealed class CalDavLiveTests
{
	private static string? Host => Environment.GetEnvironmentVariable("TEST_CALDAV_HOST");
	private static string? Port => Environment.GetEnvironmentVariable("TEST_CALDAV_PORT");
	private static string User => Environment.GetEnvironmentVariable("TEST_CALDAV_USER") ?? "test@mylomail.local";
	private static string Password => Environment.GetEnvironmentVariable("TEST_CALDAV_PASSWORD") ?? "password";

	[SkippableFact]
	public async Task An_event_can_be_created_synced_updated_and_deleted_on_a_real_server()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_CALDAV_HOST/PORT not set — start the matrix with `pnpm caldav:up`"
		);

		var baseUri = new Uri($"https://{Host}:{Port}/");
		// A distinct collection per run, so a previous run's leftovers can never be mistaken
		// for this run's baseline sync state.
		var collectionPath = $"{Uri.EscapeDataString(User)}/{Guid.NewGuid():N}/";
		var endpoint = new Uri(baseUri, collectionPath);

		using var http = new HttpClient(TrustingHandler());
		await MkCalendarAsync(http, endpoint);

		var accountId = Guid.NewGuid();
		var credentials = new InMemoryCredentialStore();
		await credentials.StoreAsync(
			accountId,
			new CredentialPayload(MailProviderFactory.ImapPasswordFormat, Encoding.UTF8.GetBytes(Password)),
			default
		);

		var account = new Account
		{
			Id = accountId,
			ProviderType = ProviderType.Imap,
			ProviderConfig = new ImapProviderConfig
			{
				CalDav = new CalDavProviderConfig { Endpoint = endpoint.ToString(), UserName = User },
			},
		};
		var calendar = new Calendar { Id = Guid.NewGuid(), ProviderCalendarId = endpoint.ToString() };

		var provider = new CalDavCalendarProvider(new CalDavRequestFactory(credentials), http);

		var created = new CalendarEventDto
		{
			ProviderEventId = "",
			ICalUid = Guid.NewGuid().ToString(),
			Title = "Standup",
			Start = DateTimeOffset.UtcNow.AddDays(1).Date,
			End = DateTimeOffset.UtcNow.AddDays(1).Date.AddHours(1),
			Status = EventStatus.Confirmed,
		};
		var href = await provider.CreateEventAsync(account, calendar, created, default);

		// A real sync-collection REPORT, not a fake's canned page: proves the provider's
		// parsing matches what this server actually sends back for a newly created event.
		var firstPage = await provider.SyncCalendarAsync(account, calendar, cursor: null, continuation: null, default);
		var upserted = Assert.Single(firstPage.Upserted);
		Assert.Equal("Standup", upserted.Title);
		Assert.Equal(created.ICalUid, upserted.ICalUid);
		Assert.NotNull(upserted.ProviderRevision);

		var toUpdate = new CalendarEvent
		{
			Id = Guid.NewGuid(),
			CalendarId = calendar.Id,
			ProviderEventId = upserted.ProviderEventId,
			ICalUid = upserted.ICalUid,
			ProviderRevision = upserted.ProviderRevision,
			Title = "Standup (moved)",
			Start = created.Start.AddHours(2),
			End = created.End.AddHours(2),
			Status = EventStatus.Confirmed,
		};
		await provider.UpdateEventAsync(account, toUpdate, upserted.ProviderRevision, default);

		var secondPage = await provider.SyncCalendarAsync(account, calendar, firstPage.NewCursor, null, default);
		var updated = Assert.Single(secondPage.Upserted);
		Assert.Equal("Standup (moved)", updated.Title);

		toUpdate.ProviderRevision = updated.ProviderRevision;
		await provider.DeleteEventAsync(account, toUpdate, default);

		var thirdPage = await provider.SyncCalendarAsync(account, calendar, secondPage.NewCursor, null, default);
		Assert.Contains(thirdPage.DeletedProviderEventIds, id => id == upserted.ProviderEventId);
	}

	private static async Task MkCalendarAsync(HttpClient http, Uri endpoint)
	{
		var request = new HttpRequestMessage(new HttpMethod("MKCALENDAR"), endpoint);
		var raw = Encoding.UTF8.GetBytes($"{User}:{Password}");
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
		using var response = await http.SendAsync(request);
		if (response.StatusCode != HttpStatusCode.Created)
		{
			throw new InvalidOperationException(
				$"Could not create the test calendar collection: {response.StatusCode}."
			);
		}
	}

	private static HttpClientHandler TrustingHandler() =>
		new()
		{
			// See the class remarks: this is the test's own client only, never the app's.
			ServerCertificateCustomValidationCallback = (
				HttpRequestMessage _,
				X509Certificate2? _,
				X509Chain? _,
				System.Net.Security.SslPolicyErrors _
			) => true,
		};
}
