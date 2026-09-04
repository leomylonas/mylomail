using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Security;
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
/// Most tests here use a client that accepts any server certificate outright — specific to
/// that one client, constructed directly rather than through
/// <c>CalendarProviderFactory</c>/DI, never touching the production trust path.
/// <see cref="Certificate_pinning_permits_a_connection_normal_validation_would_reject"/>
/// instead drives the real <see cref="CertificateTrust"/> logic against this fixture's actual
/// self-signed certificate, the same way <see cref="CalendarProviderFactory"/> wires it.
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

		// RSVP is not exercised against this fixture — it is a mail-send path, not a CalDAV
		// one — so the factory is never actually called.
		var provider = new CalDavCalendarProvider(
			new CalDavRequestFactory(credentials),
			http,
			new UnusedMailProviderFactory()
		);

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

	[SkippableFact]
	public async Task A_single_recurrence_override_can_be_edited_and_then_cancelled_without_disturbing_the_master()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_CALDAV_HOST/PORT not set — start the matrix with `pnpm caldav:up`"
		);

		var baseUri = new Uri($"https://{Host}:{Port}/");
		var endpoint = new Uri(baseUri, $"{Uri.EscapeDataString(User)}/{Guid.NewGuid():N}/");

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
		var provider = new CalDavCalendarProvider(
			new CalDavRequestFactory(credentials),
			http,
			new UnusedMailProviderFactory()
		);

		// A resource with a recurring master plus one already-existing override, as if another
		// client (or this provider, once it builds one) had already created it — CreateEventAsync
		// itself only builds single-VEVENT resources, so this is put there directly.
		var uid = Guid.NewGuid().ToString();
		var seriesIcs = $"""
			BEGIN:VCALENDAR
			VERSION:2.0
			PRODID:-//Test//EN
			BEGIN:VEVENT
			UID:{uid}
			DTSTART:20260105T090000Z
			DTEND:20260105T100000Z
			SUMMARY:Standup
			SEQUENCE:0
			RRULE:FREQ=DAILY;COUNT=5
			END:VEVENT
			BEGIN:VEVENT
			UID:{uid}
			RECURRENCE-ID:20260107T090000Z
			DTSTART:20260107T110000Z
			DTEND:20260107T120000Z
			SUMMARY:Standup (already moved)
			SEQUENCE:1
			END:VEVENT
			END:VCALENDAR
			""".ReplaceLineEndings("\r\n");
		var seriesTarget = new Uri(endpoint, $"{uid}.ics");
		await PutRawAsync(http, seriesTarget, seriesIcs);

		var firstPage = await provider.SyncCalendarAsync(account, calendar, cursor: null, continuation: null, default);
		Assert.Equal(2, firstPage.Upserted.Count);
		var master = Assert.Single(firstPage.Upserted, e => e.RecurrenceId is null);
		var overrideDto = Assert.Single(firstPage.Upserted, e => e.RecurrenceId is not null);
		Assert.Equal("Standup (already moved)", overrideDto.Title);

		var toEdit = new CalendarEvent
		{
			Id = Guid.NewGuid(),
			CalendarId = calendar.Id,
			ProviderEventId = overrideDto.ProviderEventId,
			ICalUid = overrideDto.ICalUid,
			ProviderRevision = overrideDto.ProviderRevision,
			RecurrenceMasterId = Guid.NewGuid(),
			RecurrenceId = overrideDto.RecurrenceId,
			Title = "Standup (moved again)",
			Start = overrideDto.Start.AddHours(1),
			End = overrideDto.End.AddHours(1),
			Sequence = overrideDto.Sequence + 1,
		};
		await provider.UpdateEventAsync(account, toEdit, overrideDto.ProviderRevision, default);

		// Only the edited override is a change since firstPage's cursor — the master's own
		// VEVENT block was never touched, which is exactly the property this merge exists to
		// preserve, and a full second copy of it here would mean it silently was.
		var secondPage = await provider.SyncCalendarAsync(account, calendar, firstPage.NewCursor, null, default);
		var overrideAfter = Assert.Single(secondPage.Upserted, e => e.RecurrenceId is not null);
		Assert.Equal("Standup (moved again)", overrideAfter.Title);
		Assert.DoesNotContain(secondPage.Upserted, e => e.RecurrenceId is null && e.Title != "Standup");

		// The master, read independently, is still exactly as it was.
		var freshSync = await provider.SyncCalendarAsync(account, calendar, cursor: null, continuation: null, default);
		var masterStillIntact = Assert.Single(freshSync.Upserted, e => e.RecurrenceId is null);
		Assert.Equal("Standup", masterStillIntact.Title);

		// "Deleting" this same override cancels it in place rather than removing the series —
		// a resource-level DELETE here would take the master and the rest of the recurrence
		// with it, since they all share one .ics resource.
		toEdit.ProviderRevision = overrideAfter.ProviderRevision;
		await provider.DeleteEventAsync(account, toEdit, default);

		var thirdPage = await provider.SyncCalendarAsync(account, calendar, secondPage.NewCursor, null, default);
		var cancelled = Assert.Single(thirdPage.Upserted, e => e.RecurrenceId is not null);
		Assert.Equal(EventStatus.Cancelled, cancelled.Status);
		Assert.Empty(thirdPage.DeletedProviderEventIds);

		var finalSync = await provider.SyncCalendarAsync(account, calendar, cursor: null, continuation: null, default);
		var masterAfterCancel = Assert.Single(finalSync.Upserted, e => e.RecurrenceId is null);
		Assert.Equal("Standup", masterAfterCancel.Title);
	}

	[SkippableFact]
	public async Task Certificate_pinning_permits_a_connection_normal_validation_would_reject()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_CALDAV_HOST/PORT not set — start the matrix with `pnpm caldav:up`"
		);

		var baseUri = new Uri($"https://{Host}:{Port}/");
		var endpoint = new Uri(baseUri, $"{Uri.EscapeDataString(User)}/{Guid.NewGuid():N}/");

		// Set up the collection with a trusting client — the point of this test is what
		// happens on the *next* connection, not this setup step.
		using (var setup = new HttpClient(TrustingHandler()))
		{
			await MkCalendarAsync(setup, endpoint);
		}

		var credentials = new InMemoryCredentialStore();
		var accountId = Guid.NewGuid();
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

		// The exact wiring CalendarProviderFactory does: real validation first, a pinned
		// fingerprint only consulted once that has already failed (self-signed, so it always
		// does here). Whatever the server actually presented is captured regardless of the
		// verdict, standing in for what an AddAccount attempt would show the user to pin.
		string? presentedFingerprint = null;
		HttpClient ClientFor(CertificateTrustMode mode, IReadOnlyList<AccountTrustedCertificate> pinned) =>
			new(
				new HttpClientHandler
				{
					ServerCertificateCustomValidationCallback = (request, certificate, _, sslPolicyErrors) =>
					{
						if (certificate is null)
						{
							return false;
						}
						presentedFingerprint = CertificateTrust.Fingerprint(certificate);
						return CertificateTrust.Validate(
							mode,
							pinned,
							request.RequestUri!.Host,
							certificate,
							sslPolicyErrors
						);
					},
				}
			);

		using (var rejecting = ClientFor(CertificateTrustMode.Default, []))
		{
			var provider = new CalDavCalendarProvider(new CalDavRequestFactory(credentials), rejecting, new UnusedMailProviderFactory());
			await Assert.ThrowsAsync<HttpRequestException>(
				() => provider.ListCalendarsAsync(account, default)
			);
		}

		Assert.NotNull(presentedFingerprint);

		var pinnedEntry = new AccountTrustedCertificate
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			ExpectedHostname = Host!,
			Sha256Fingerprint = presentedFingerprint!,
		};

		using (var pinned = ClientFor(CertificateTrustMode.Default, [pinnedEntry]))
		{
			var provider = new CalDavCalendarProvider(new CalDavRequestFactory(credentials), pinned, new UnusedMailProviderFactory());
			// No exception: the pin is exactly what CertificateTrust.Problem's Extensions would
			// have handed TrustCertificate to record.
			await provider.ListCalendarsAsync(account, default);
		}

		using (var trustAll = ClientFor(CertificateTrustMode.TrustAll, []))
		{
			var provider = new CalDavCalendarProvider(new CalDavRequestFactory(credentials), trustAll, new UnusedMailProviderFactory());
			await provider.ListCalendarsAsync(account, default);
		}
	}

	/// <summary>
	/// A rejected certificate must reach the caller as something a user can act on — the real
	/// fingerprint, hostname, and issuer <c>TrustCertificate</c> needs — not a bare
	/// <see cref="HttpRequestException"/>. Drives <see cref="CalendarProviderFactory"/> itself
	/// (the production wiring, not a client built by hand), against this fixture's real
	/// self-signed certificate, so this proves the same translation IMAP's
	/// <c>AuthenticateAsync</c> already gets.
	/// </summary>
	[SkippableFact]
	public async Task A_rejected_certificate_is_reported_with_its_fingerprint_not_a_bare_transport_error()
	{
		Skip.If(
			string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Port),
			"TEST_CALDAV_HOST/PORT not set — start the matrix with `pnpm caldav:up`"
		);

		var baseUri = new Uri($"https://{Host}:{Port}/");
		var endpoint = new Uri(baseUri, $"{Uri.EscapeDataString(User)}/{Guid.NewGuid():N}/");

		using (var setup = new HttpClient(TrustingHandler()))
		{
			await MkCalendarAsync(setup, endpoint);
		}

		var credentials = new InMemoryCredentialStore();
		var accountId = Guid.NewGuid();
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

		var factory = new CalendarProviderFactory(
			credentials,
			new UnusedMailProviderFactory(),
			new EmptyTrustedCertificateStore()
		);
		var provider = factory.For(account);
		using var disposable = provider as IDisposable;

		var ex = await Assert.ThrowsAsync<ProviderAuthenticationException>(
			() => provider.ListCalendarsAsync(account, default)
		);
		Assert.Contains(Host!, ex.Message);
		Assert.Matches("[0-9a-f]{64}", ex.Message);
	}

	private sealed class EmptyTrustedCertificateStore : ITrustedCertificateStore
	{
		public IReadOnlyList<AccountTrustedCertificate> GetForAccount(Guid accountId) => [];

		public Task TrustAsync(Guid accountId, string expectedHostname, string sha256Fingerprint, CancellationToken ct) =>
			throw new NotSupportedException();
	}

	private static async Task PutRawAsync(HttpClient http, Uri target, string ics)
	{
		var request = new HttpRequestMessage(HttpMethod.Put, target)
		{
			Content = new StringContent(ics, Encoding.UTF8, "text/calendar"),
		};
		var raw = Encoding.UTF8.GetBytes($"{User}:{Password}");
		request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(raw));
		using var response = await http.SendAsync(request);
		response.EnsureSuccessStatusCode();
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

	private sealed class UnusedMailProviderFactory : IMailProviderFactory
	{
		public IMailProvider For(Account account) => throw new NotSupportedException();
	}
}
