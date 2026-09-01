using System.Net;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>
/// CalDAV, over the WebDAV primitives in this folder. Genuinely generic: configured
/// independently on a plain IMAP account, against whatever server the user points it at (§1).
/// </summary>
/// <remarks>
/// <para>
/// One calendar collection per account is supported — the configured endpoint is itself the
/// collection, not a calendar-home-set to discover children under. Multiple calendars on one
/// CalDAV server would need discovery via the calendar-home-set PROPFIND, which is deferred
/// alongside RSVP/iTIP (both are additional protocol surface beyond CRUD and sync).
/// </para>
/// <para>
/// Incremental sync uses the WebDAV-sync REPORT (RFC 6578): the server hands back a
/// <c>sync-token</c> covering everything returned, deleted members arrive as 404 responses,
/// and an expired token is surfaced as <see cref="ProviderCursorInvalidException"/> so §3's
/// baseline-reset path handles it uniformly with IMAP/Graph/Gmail cursor invalidation. A
/// server that does not support sync-collection cannot be synced incrementally by this
/// provider — full recurring calendar polling is out of scope for this pass.
/// </para>
/// <para>
/// A recurrence override instance's provider event id is its resource href plus its
/// <c>RECURRENCE-ID</c>, because several <c>VEVENT</c>s share one .ics resource and one
/// href. <see cref="UpdateEventAsync"/> and <see cref="DeleteEventAsync"/> therefore only
/// operate at the resource (master) level — editing a single override in place is deferred
/// with RSVP, since both need the same multi-VEVENT PUT this pass does not build.
/// </para>
/// </remarks>
public sealed class CalDavCalendarProvider(CalDavRequestFactory requests, HttpClient http) : ICalendarProvider
{
	private static readonly HttpMethod PropFind = new("PROPFIND");
	private static readonly HttpMethod Report = new("REPORT");

	public ProviderType Type => ProviderType.Imap;

	public async Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct)
	{
		var endpoint = Endpoint(account);
		var request = await requests.CreateAsync(account, PropFind, ct: ct);
		CalDavWebDavRequest.SetDepth(request, "0");
		request.Content = CalDavWebDavRequest.Xml(
			"""<?xml version="1.0" encoding="utf-8" ?><D:propfind xmlns:D="DAV:"><D:prop><D:displayname/></D:prop></D:propfind>"""
		);

		using var response = await http.SendAsync(request, ct);
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(ct);
		var name = CalDavMultiStatusParser.Parse(body).FirstOrDefault()?.DisplayName ?? "Calendar";

		return [new CalendarDto(endpoint.ToString(), name, null, true)];
	}

	public async Task<CalendarSyncResult> SyncCalendarAsync(
		Account account,
		Calendar calendar,
		string? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		var request = await requests.CreateAsync(account, Report, ct: ct);
		CalDavWebDavRequest.SetDepth(request, "1");
		request.Content = CalDavWebDavRequest.Xml(
			$"""
			<?xml version="1.0" encoding="utf-8" ?>
			<D:sync-collection xmlns:D="DAV:">
			<D:sync-token>{System.Security.SecurityElement.Escape(cursor ?? string.Empty)}</D:sync-token>
			<D:sync-level>1</D:sync-level>
			<D:prop><D:getetag/><C:calendar-data xmlns:C="urn:ietf:params:xml:ns:caldav"/></D:prop>
			</D:sync-collection>
			"""
		);

		using var response = await http.SendAsync(request, ct);
		if (
			cursor is not null
			&& response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
		)
		{
			throw new ProviderCursorInvalidException($"The CalDAV sync-token was rejected ({response.StatusCode}).");
		}
		response.EnsureSuccessStatusCode();
		var body = await response.Content.ReadAsStringAsync(ct);

		var newCursor = CalDavMultiStatusParser.SyncToken(body);
		if (string.IsNullOrEmpty(newCursor))
		{
			throw new InvalidOperationException(
				"The CalDAV server did not return a sync-token; it does not support RFC 6578 sync-collection."
			);
		}

		var upserted = new List<CalendarEventDto>();
		var deleted = new List<string>();
		foreach (var member in CalDavMultiStatusParser.Parse(body))
		{
			if (member.IsDeleted)
			{
				deleted.Add(member.Href);
				continue;
			}
			if (member.CalendarData is { Length: > 0 } data)
			{
				upserted.AddRange(CalDavIcs.ParseEvents(data, member.Href, member.ETag ?? string.Empty));
			}
		}

		return new CalendarSyncResult(newCursor, null, upserted, deleted);
	}

	public async Task<string> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct)
	{
		var target = new Uri(Endpoint(account), $"{ev.ICalUid}.ics");
		var request = await requests.CreateAsync(account, HttpMethod.Put, target, ct);
		request.Headers.TryAddWithoutValidation("If-None-Match", "*");
		request.Content = new StringContent(CalDavIcs.ToIcs(ev.ICalUid, ev), System.Text.Encoding.UTF8, "text/calendar");

		using var response = await http.SendAsync(request, ct);
		response.EnsureSuccessStatusCode();
		return (response.Headers.Location ?? target).ToString();
	}

	public async Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct)
	{
		var target = ResourceHref(account, ev.ProviderEventId);
		var request = await requests.CreateAsync(account, HttpMethod.Put, target, ct);
		CalDavWebDavRequest.SetIfMatch(request, expectedETag);
		request.Content = new StringContent(CalDavIcs.ToIcs(ev.ICalUid, ToDto(ev)), System.Text.Encoding.UTF8, "text/calendar");

		using var response = await http.SendAsync(request, ct);
		if (response.StatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The CalDAV event changed on the server since it was last read.");
		}
		response.EnsureSuccessStatusCode();
	}

	public async Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct)
	{
		var target = ResourceHref(account, ev.ProviderEventId);
		var request = await requests.CreateAsync(account, HttpMethod.Delete, target, ct);
		CalDavWebDavRequest.SetIfMatch(request, ev.ProviderRevision);

		using var response = await http.SendAsync(request, ct);
		if (response.StatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The CalDAV event changed on the server since it was last read.");
		}
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return;
		}
		response.EnsureSuccessStatusCode();
	}

	/// <summary>
	/// RSVP over CalDAV/IMAP is an iTIP <c>REPLY</c> sent as mail, a distinct piece of work
	/// from calendar CRUD (§1, §13 Epic 7) and not yet built.
	/// </summary>
	public Task RespondToInviteAsync(Account account, CalendarEvent ev, InviteResponse response, string? comment, CancellationToken ct) =>
		throw new NotSupportedException("CalDAV invite RSVP (iTIP REPLY) is not yet implemented.");

	private static Uri Endpoint(Account account) =>
		account.ProviderConfig is ImapProviderConfig { CalDav: { } config }
			? new Uri(config.Endpoint)
			: throw new ProviderNotConfiguredException(ProviderType.Imap, "CalDAV endpoint configuration");

	/// <summary>
	/// Resolves a stored <c>ProviderEventId</c> to the URI a request is actually sent to,
	/// stripping a recurrence-override suffix first (master and override share one resource).
	/// </summary>
	/// <remarks>
	/// A sync page's href is exactly what the multi-status response said — a server-relative
	/// path, not an absolute URI (see <see cref="CalDavMultiStatusParser"/> and the unit tests
	/// against it) — and <see cref="Sync.CalendarSyncService"/> persists it verbatim. Passing
	/// that straight to <c>new Uri(string)</c> with no base does not throw on Unix: a string
	/// starting with <c>/</c> is happily parsed as a <c>file://</c> URI, and the request then
	/// fails with "The 'file' scheme is not supported" instead of reaching the server at all.
	/// Resolving against the account's own endpoint handles both a relative href from a sync
	/// page and an already-absolute one from <see cref="CreateEventAsync"/>'s return value
	/// identically — <see cref="Uri(Uri, string)"/> ignores the base when the second argument
	/// is already absolute.
	/// </remarks>
	private static Uri ResourceHref(Account account, string providerEventId) =>
		new(Endpoint(account), providerEventId.Split('#')[0]);

	private static CalendarEventDto ToDto(CalendarEvent ev) => new()
	{
		ProviderEventId = ev.ProviderEventId,
		ICalUid = ev.ICalUid,
		ProviderRevision = ev.ProviderRevision,
		Sequence = ev.Sequence,
		Title = ev.Title,
		Location = ev.Location,
		Description = ev.Description,
		Start = ev.Start,
		End = ev.End,
		StartTimeZoneId = ev.StartTimeZoneId,
		EndTimeZoneId = ev.EndTimeZoneId,
		IsAllDay = ev.IsAllDay,
		Organizer = ev.Organizer,
		Attendees = ev.Attendees,
		Status = ev.Status,
		Reminders = ev.Reminders,
		RecurrenceRules = ev.RecurrenceRules,
		RecurrenceDates = ev.RecurrenceDates,
		ExceptionDates = ev.ExceptionDates,
		RecurrenceMasterProviderEventId = ev.RecurrenceMasterProviderEventId,
		RecurrenceId = ev.RecurrenceId,
	};
}
