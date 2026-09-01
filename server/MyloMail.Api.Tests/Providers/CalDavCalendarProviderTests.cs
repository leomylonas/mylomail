using System.Net;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
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

	private static CalDavCalendarProvider Provider(HttpMessageHandler handler)
	{
		var store = new InMemoryCredentialStore();
		store.StoreAsync(
			AccountId,
			new CredentialPayload(MailProviderFactory.ImapPasswordFormat, "imap-secret"u8.ToArray()),
			default
		).GetAwaiter().GetResult();
		return new CalDavCalendarProvider(new CalDavRequestFactory(store), new HttpClient(handler));
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
