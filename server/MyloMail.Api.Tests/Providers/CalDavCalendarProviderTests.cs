using System.Net;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Tests.Fakes;
using Xunit;

namespace MyloMail.Api.Tests.Providers;

public sealed class CalDavCalendarProviderTests
{
	private const string Endpoint = "https://calendar.example.test/dav/personal/";

	[Fact]
	public async Task Lists_the_configured_endpoint_as_one_calendar()
	{
		var handler = new FakeHandler();
		handler.Enqueue(MultiStatus("""<D:response><D:href>/dav/personal/</D:href><D:propstat><D:prop><D:displayname>Personal</D:displayname></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>"""));
		var provider = Provider(handler);

		var calendars = await provider.ListCalendarsAsync(Account(), default);

		var calendar = Assert.Single(calendars);
		Assert.Equal("Personal", calendar.Name);
		Assert.Equal(Endpoint, calendar.ProviderCalendarId);
		Assert.True(calendar.IsDefault);
		Assert.Equal("PROPFIND", handler.Requests.Single().Method.Method);
	}

	[Fact]
	public async Task A_sync_collection_report_carries_the_new_cursor_and_upserted_events()
	{
		var handler = new FakeHandler();
		var ics = """
			BEGIN:VCALENDAR
			BEGIN:VEVENT
			UID:event-1
			DTSTART:20260101T090000Z
			DTEND:20260101T100000Z
			SUMMARY:Standup
			SEQUENCE:0
			STATUS:CONFIRMED
			END:VEVENT
			END:VCALENDAR
			""".ReplaceLineEndings("\r\n");
		handler.Enqueue(
			MultiStatus(
				$"""<D:response><D:href>/dav/personal/event-1.ics</D:href><D:propstat><D:prop><D:getetag>"etag-1"</D:getetag><C:calendar-data xmlns:C="urn:ietf:params:xml:ns:caldav">{ics}</C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>""",
				syncToken: "token-1"
			)
		);
		var provider = Provider(handler);

		var page = await provider.SyncCalendarAsync(Account(), Calendar(), cursor: null, continuation: null, default);

		Assert.Equal("token-1", page.NewCursor);
		Assert.Null(page.Continuation);
		var upserted = Assert.Single(page.Upserted);
		Assert.Equal("Standup", upserted.Title);
		Assert.Equal("/dav/personal/event-1.ics", upserted.ProviderEventId);
		Assert.Equal("\"etag-1\"", upserted.ProviderRevision);
		Assert.Equal(new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero), upserted.Start);
		Assert.Equal("REPORT", handler.Requests.Single().Method.Method);
	}

	[Fact]
	public async Task A_404_member_in_a_sync_report_is_a_deletion()
	{
		var handler = new FakeHandler();
		handler.Enqueue(
			MultiStatus(
				"""<D:response><D:href>/dav/personal/gone.ics</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>""",
				syncToken: "token-2"
			)
		);
		var provider = Provider(handler);

		var page = await provider.SyncCalendarAsync(Account(), Calendar(), "token-1", null, default);

		Assert.Empty(page.Upserted);
		Assert.Equal(["/dav/personal/gone.ics"], page.DeletedProviderEventIds);
	}

	/// <summary>
	/// A rejected Basic-auth credential must surface as <see cref="ProviderAuthenticationException"/>,
	/// not a raw <see cref="HttpRequestException"/> from <c>EnsureSuccessStatusCode</c> — the
	/// latter is unconditionally classified as a transient network blip by
	/// <see cref="Scheduling.ConnectivityMonitor.IsNetworkFailure"/>, which would retry a wrong
	/// or revoked password forever instead of ever setting <c>AuthState.NeedsReauth</c>.
	/// </summary>
	[Fact]
	public async Task A_401_response_surfaces_as_a_provider_authentication_failure()
	{
		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") });
		var provider = Provider(handler);

		await Assert.ThrowsAsync<ProviderAuthenticationException>(
			() => provider.ListCalendarsAsync(Account(), default)
		);
	}

	[Fact]
	public async Task A_rejected_sync_token_is_reported_as_an_invalid_cursor()
	{
		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("") });
		var provider = Provider(handler);

		await Assert.ThrowsAsync<ProviderCursorInvalidException>(
			() => provider.SyncCalendarAsync(Account(), Calendar(), "stale-token", null, default)
		);
	}

	[Fact]
	public async Task Creating_an_event_puts_a_resource_the_sync_report_parser_can_read_back()
	{
		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") });
		var provider = Provider(handler);
		var ev = new CalendarEventDto
		{
			ProviderEventId = "",
			ICalUid = "new-event",
			Title = "Planning",
			Start = new DateTimeOffset(2026, 3, 1, 14, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 3, 1, 15, 0, 0, TimeSpan.Zero),
		};

		var providerEventId = await provider.CreateEventAsync(Account(), Calendar(), ev, default);

		Assert.Equal(Endpoint + "new-event.ics", providerEventId);
		var request = handler.Requests.Single();
		Assert.Equal(HttpMethod.Put, request.Method);
		Assert.Equal("*", request.Headers.GetValues("If-None-Match").Single());
		var body = await request.Content!.ReadAsStringAsync();

		// Fed back as a sync-report response, proving CreateEventAsync writes exactly what
		// SyncCalendarAsync's parser expects, rather than asserting on the ICS text directly.
		handler.Enqueue(
			MultiStatus(
				$"""<D:response><D:href>{providerEventId}</D:href><D:propstat><D:prop><D:getetag>"etag-new"</D:getetag><C:calendar-data xmlns:C="urn:ietf:params:xml:ns:caldav">{body}</C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>""",
				syncToken: "token-new"
			)
		);
		var page = await provider.SyncCalendarAsync(Account(), Calendar(), null, null, default);
		var parsed = Assert.Single(page.Upserted);
		Assert.Equal("Planning", parsed.Title);
		Assert.Equal(ev.Start, parsed.Start);
		Assert.Equal(ev.End, parsed.End);
	}

	[Fact]
	public async Task Punctuation_in_text_fields_round_trips_through_create_and_sync()
	{
		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") });
		var provider = Provider(handler);
		var ev = new CalendarEventDto
		{
			ProviderEventId = "",
			ICalUid = "punctuated",
			Title = "Budget, Q1; review\nfollow-up",
			Start = DateTimeOffset.UnixEpoch,
			End = DateTimeOffset.UnixEpoch.AddHours(1),
		};

		var providerEventId = await provider.CreateEventAsync(Account(), Calendar(), ev, default);
		var body = await handler.Requests.Single().Content!.ReadAsStringAsync();

		handler.Enqueue(
			MultiStatus(
				$"""<D:response><D:href>{providerEventId}</D:href><D:propstat><D:prop><D:getetag>"etag"</D:getetag><C:calendar-data xmlns:C="urn:ietf:params:xml:ns:caldav">{body}</C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>""",
				syncToken: "token"
			)
		);
		var page = await provider.SyncCalendarAsync(Account(), Calendar(), null, null, default);

		Assert.Equal(ev.Title, Assert.Single(page.Upserted).Title);
	}

	[Fact]
	public async Task Updating_an_event_sends_if_match_and_surfaces_a_precondition_failure_as_a_conflict()
	{
		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.PreconditionFailed) { Content = new StringContent("") });
		var provider = Provider(handler);

		await Assert.ThrowsAsync<ProviderConflictException>(
			() => provider.UpdateEventAsync(Account(), Event(), "\"stale-etag\"", default)
		);

		Assert.Equal("\"stale-etag\"", handler.Requests.Single().Headers.IfMatch.Single().Tag);
	}

	[Fact]
	public async Task Deleting_an_already_deleted_event_is_not_an_error()
	{
		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") });
		var provider = Provider(handler);

		await provider.DeleteEventAsync(Account(), Event(), default);

		Assert.Equal(HttpMethod.Delete, handler.Requests.Single().Method);
	}

	[Fact]
	public async Task Updating_a_recurrence_override_merges_it_into_the_shared_resource_without_disturbing_siblings()
	{
		var resourceIcs = """
			BEGIN:VCALENDAR
			VERSION:2.0
			BEGIN:VEVENT
			UID:series
			DTSTART:20260101T090000Z
			DTEND:20260101T100000Z
			SUMMARY:Standup
			SEQUENCE:0
			RRULE:FREQ=DAILY
			END:VEVENT
			BEGIN:VEVENT
			UID:series
			RECURRENCE-ID:20260103T090000Z
			DTSTART:20260103T110000Z
			DTEND:20260103T120000Z
			SUMMARY:Standup (moved once already)
			SEQUENCE:1
			END:VEVENT
			END:VCALENDAR
			""".ReplaceLineEndings("\r\n");

		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resourceIcs) });
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
		var provider = Provider(handler);

		var overrideEvent = new CalendarEvent
		{
			Id = Guid.NewGuid(),
			CalendarId = Guid.NewGuid(),
			ProviderEventId = $"{Endpoint}series.ics#{new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero):O}",
			ICalUid = "series",
			RecurrenceMasterId = Guid.NewGuid(),
			RecurrenceId = new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero),
			Title = "Standup (moved)",
			Start = new DateTimeOffset(2026, 1, 3, 13, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 1, 3, 14, 0, 0, TimeSpan.Zero),
			Sequence = 1,
		};

		await provider.UpdateEventAsync(Account(), overrideEvent, "\"resource-etag\"", default);

		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
		var put = handler.Requests[1];
		Assert.Equal(HttpMethod.Put, put.Method);
		// The resource-level revision the caller already had, not anything the GET observed —
		// see UpdateOverrideAsync's remarks on why those must not be the same value.
		Assert.Equal("\"resource-etag\"", put.Headers.IfMatch.Single().Tag);

		var body = await put.Content!.ReadAsStringAsync();
		Assert.Contains("SUMMARY:Standup\r\n", body); // the master, untouched
		Assert.Contains("Standup (moved)", body); // the newly merged override
		Assert.DoesNotContain("moved once already", body); // the old override content is gone
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, "BEGIN:VEVENT").Count);
	}

	[Fact]
	public async Task Deleting_a_recurrence_override_cancels_it_in_place_rather_than_removing_the_series()
	{
		var resourceIcs = """
			BEGIN:VCALENDAR
			VERSION:2.0
			BEGIN:VEVENT
			UID:series
			DTSTART:20260101T090000Z
			DTEND:20260101T100000Z
			SUMMARY:Standup
			SEQUENCE:0
			RRULE:FREQ=DAILY
			END:VEVENT
			BEGIN:VEVENT
			UID:series
			RECURRENCE-ID:20260103T090000Z
			DTSTART:20260103T110000Z
			DTEND:20260103T120000Z
			SUMMARY:Standup (moved)
			SEQUENCE:1
			END:VEVENT
			END:VCALENDAR
			""".ReplaceLineEndings("\r\n");

		var handler = new FakeHandler();
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resourceIcs) });
		handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NoContent));
		var provider = Provider(handler);

		var overrideEvent = new CalendarEvent
		{
			Id = Guid.NewGuid(),
			CalendarId = Guid.NewGuid(),
			ProviderEventId = $"{Endpoint}series.ics#{new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero):O}",
			ICalUid = "series",
			ProviderRevision = "\"resource-etag\"",
			RecurrenceMasterId = Guid.NewGuid(),
			RecurrenceId = new DateTimeOffset(2026, 1, 3, 9, 0, 0, TimeSpan.Zero),
			Title = "Standup (moved)",
			Start = new DateTimeOffset(2026, 1, 3, 11, 0, 0, TimeSpan.Zero),
			End = new DateTimeOffset(2026, 1, 3, 12, 0, 0, TimeSpan.Zero),
			Sequence = 1,
		};

		await provider.DeleteEventAsync(Account(), overrideEvent, default);

		Assert.Equal(2, handler.Requests.Count);
		Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
		var put = handler.Requests[1];
		Assert.Equal(HttpMethod.Put, put.Method);
		Assert.Equal("\"resource-etag\"", put.Headers.IfMatch.Single().Tag);

		var body = await put.Content!.ReadAsStringAsync();
		Assert.Contains("SUMMARY:Standup\r\n", body); // the master, untouched
		Assert.Contains("STATUS:CANCELLED", body); // the override, cancelled rather than removed
		Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(body, "BEGIN:VEVENT").Count);
	}

	[Fact]
	public async Task Responding_to_an_invite_sends_an_itip_reply_to_the_organiser()
	{
		var mail = new FakeMailProvider(ProviderShapes.Imap(ImapCapabilityTier.QResync));
		var provider = Provider(new FakeHandler(), mail);
		var ev = Event();
		ev.Organizer = new Address("Alice Organiser", "alice@example.test");

		await provider.RespondToInviteAsync(
			Account(),
			ev,
			InviteResponse.Decline,
			comment: null,
			replyingAs: new Address("Bob Attendee", "bob@example.test"),
			ct: default
		);

		var sent = mail.LastSentDraft;
		Assert.NotNull(sent);
		Assert.Equal("bob@example.test", sent!.FromAddress);
		Assert.Equal("alice@example.test", Assert.Single(sent.To).Email);
		Assert.Equal("Declined: Existing", sent.Subject);

		var attachment = Assert.Single(sent.Attachments);
		Assert.StartsWith("text/calendar", attachment.MimeType);
		var ics = System.Text.Encoding.UTF8.GetString(attachment.Content);
		Assert.Contains("METHOD:REPLY", ics);
		Assert.Contains("PARTSTAT=DECLINED", ics);
		Assert.Contains("mailto:bob@example.test", ics);
		Assert.Contains("mailto:alice@example.test", ics);
	}

	private static CalDavCalendarProvider Provider(HttpMessageHandler handler, IMailProvider? mailProvider = null)
	{
		var store = new InMemoryCredentialStore();
		store.StoreAsync(
			AccountId,
			new CredentialPayload(MailProviderFactory.ImapPasswordFormat, "imap-secret"u8.ToArray()),
			default
		).GetAwaiter().GetResult();
		return new CalDavCalendarProvider(
			new CalDavRequestFactory(store),
			new HttpClient(handler),
			new SingleMailProviderFactory(mailProvider ?? new FakeMailProvider(ProviderShapes.Imap(ImapCapabilityTier.QResync)))
		);
	}

	private sealed class SingleMailProviderFactory(IMailProvider provider) : IMailProviderFactory
	{
		public IMailProvider For(Account account) => provider;
	}

	private static readonly Guid AccountId = Guid.NewGuid();

	private static Account Account() => new()
	{
		Id = AccountId,
		ProviderType = ProviderType.Imap,
		ProviderConfig = new ImapProviderConfig
		{
			CalDav = new CalDavProviderConfig { Endpoint = Endpoint, UserName = "caldav-user" },
		},
	};

	private static Calendar Calendar() => new() { Id = Guid.NewGuid(), ProviderCalendarId = Endpoint };

	private static CalendarEvent Event() => new()
	{
		Id = Guid.NewGuid(),
		ProviderEventId = Endpoint + "existing.ics",
		ICalUid = "existing",
		Title = "Existing",
		Start = DateTimeOffset.UnixEpoch,
		End = DateTimeOffset.UnixEpoch.AddHours(1),
	};

	private static HttpResponseMessage MultiStatus(string responses, string? syncToken = null) =>
		new(HttpStatusCode.MultiStatus)
		{
			Content = new StringContent(
				$"""<?xml version="1.0"?><D:multistatus xmlns:D="DAV:">{responses}{(syncToken is null ? "" : $"<D:sync-token>{syncToken}</D:sync-token>")}</D:multistatus>"""
			),
		};

	private sealed class FakeHandler : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> responses = new();

		public List<HttpRequestMessage> Requests { get; } = [];

		public void Enqueue(HttpResponseMessage response) => responses.Enqueue(response);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
		{
			Requests.Add(request);
			return Task.FromResult(responses.Dequeue());
		}
	}
}
