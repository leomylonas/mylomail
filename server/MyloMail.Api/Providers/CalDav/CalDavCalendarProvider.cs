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
/// <c>RECURRENCE-ID</c>, because several <c>VEVENT</c>s share one .ics resource and one href.
/// Editing (<see cref="UpdateEventAsync"/>) or "deleting" (<see cref="DeleteEventAsync"/>, which
/// marks it <c>STATUS:CANCELLED</c> rather than removing anything) one in place, when
/// <c>ev.RecurrenceMasterId</c> is set, therefore GETs the current resource, merges just that
/// one <c>VEVENT</c> via <see cref="CalDavIcs.MergeOverride"/>, and PUTs the whole resource back
/// — a naive single-<c>VEVENT</c> PUT or a resource-level DELETE (the master-level paths every
/// other update/delete uses) would silently discard the master and every other override sharing
/// the resource.
/// </para>
/// </remarks>
public sealed class CalDavCalendarProvider(
	CalDavRequestFactory requests,
	HttpClient http,
	IMailProviderFactory mail
) : ICalendarProvider, IDisposable
{
	private static readonly HttpMethod PropFind = new("PROPFIND");
	private static readonly HttpMethod Report = new("REPORT");

	/// <summary>
	/// <see cref="CalendarProviderFactory"/> hands out a fresh <see cref="HttpClient"/> per
	/// account per resolution (§15 — certificate trust is a per-account decision, so its
	/// handler can't be the app-wide pooled one), so this is what actually releases it rather
	/// than leaving cleanup to the finalizer.
	/// </summary>
	public void Dispose() => http.Dispose();

	public ProviderType Type => ProviderType.Imap;

	/// <summary>
	/// Every CalDAV request this provider sends routes through here, so a rejected Basic-auth
	/// credential is translated into <see cref="ProviderAuthenticationException"/> exactly once
	/// rather than at each of the eight call sites below — matching §"Auth failures set
	/// AuthState = NeedsReauth" for CalDAV the same way passes 196-198 already did for IMAP,
	/// SMTP, Gmail and Graph. Left unhandled, a 401 reaches <c>EnsureSuccessStatusCode</c> as a
	/// generic <see cref="HttpRequestException"/>, which <see cref="Scheduling.ConnectivityMonitor.IsNetworkFailure"/>
	/// unconditionally classifies as a transient network blip — a wrong or revoked CalDAV
	/// password would then retry silently forever instead of ever pausing the account's jobs.
	/// </summary>
	private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
	{
		var response = await http.SendAsync(request, ct);
		if (response.StatusCode == HttpStatusCode.Unauthorized)
		{
			response.Dispose();
			throw new ProviderAuthenticationException("The CalDAV server rejected these credentials.");
		}
		return response;
	}

	public async Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct)
	{
		var endpoint = Endpoint(account);
		var request = await requests.CreateAsync(account, PropFind, ct: ct);
		CalDavWebDavRequest.SetDepth(request, "0");
		request.Content = CalDavWebDavRequest.Xml(
			"""<?xml version="1.0" encoding="utf-8" ?><D:propfind xmlns:D="DAV:"><D:prop><D:displayname/></D:prop></D:propfind>"""
		);

		using var response = await SendAsync(request, ct);
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

		using var response = await SendAsync(request, ct);
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

		using var response = await SendAsync(request, ct);
		response.EnsureSuccessStatusCode();
		return (response.Headers.Location ?? target).ToString();
	}

	public async Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct)
	{
		var target = ResourceHref(account, ev.ProviderEventId);

		if (ev.RecurrenceMasterId is not null)
		{
			await UpdateOverrideAsync(account, ev, target, expectedETag, ct);
			return;
		}

		var request = await requests.CreateAsync(account, HttpMethod.Put, target, ct);
		CalDavWebDavRequest.SetIfMatch(request, expectedETag);
		request.Content = new StringContent(CalDavIcs.ToIcs(ev.ICalUid, ToDto(ev)), System.Text.Encoding.UTF8, "text/calendar");

		using var response = await SendAsync(request, ct);
		if (response.StatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The CalDAV event changed on the server since it was last read.");
		}
		response.EnsureSuccessStatusCode();
	}

	/// <summary>
	/// Updates one override instance without disturbing its siblings: GET the current
	/// resource content to merge into, then PUT the whole thing back.
	/// </summary>
	/// <remarks>
	/// <paramref name="expectedETag"/> — not whatever the GET just observed — is what goes in
	/// <c>If-Match</c>. A resource's <c>getetag</c> is shared by every <c>VEVENT</c> inside it,
	/// so this override's own stored revision already <i>is</i> the resource-level one (§1); an
	/// If-Match built from a same-request GET would always match and detect no conflict at all,
	/// since fetching immediately before writing cannot observe an edit that happens in between.
	/// </remarks>
	private async Task UpdateOverrideAsync(
		Account account,
		CalendarEvent ev,
		Uri target,
		string? expectedETag,
		CancellationToken ct
	)
	{
		var getRequest = await requests.CreateAsync(account, HttpMethod.Get, target, ct);
		using var getResponse = await SendAsync(getRequest, ct);
		if (getResponse.StatusCode == HttpStatusCode.NotFound)
		{
			throw new ProviderConflictException("The recurring event this instance belongs to no longer exists.");
		}
		getResponse.EnsureSuccessStatusCode();
		var currentIcs = await getResponse.Content.ReadAsStringAsync(ct);

		var merged = CalDavIcs.MergeOverride(currentIcs, ev.ICalUid, ToDto(ev));

		var putRequest = await requests.CreateAsync(account, HttpMethod.Put, target, ct);
		CalDavWebDavRequest.SetIfMatch(putRequest, expectedETag);
		putRequest.Content = new StringContent(merged, System.Text.Encoding.UTF8, "text/calendar");

		using var putResponse = await SendAsync(putRequest, ct);
		if (putResponse.StatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The CalDAV event changed on the server since it was last read.");
		}
		putResponse.EnsureSuccessStatusCode();
	}

	public async Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct)
	{
		var target = ResourceHref(account, ev.ProviderEventId);

		if (ev.RecurrenceMasterId is not null)
		{
			await CancelOverrideAsync(account, ev, target, ct);
			return;
		}

		var request = await requests.CreateAsync(account, HttpMethod.Delete, target, ct);
		CalDavWebDavRequest.SetIfMatch(request, ev.ProviderRevision);

		using var response = await SendAsync(request, ct);
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
	/// "Deletes" one recurrence-override instance by marking it cancelled in place, the same
	/// merge <see cref="UpdateOverrideAsync"/> uses — DELETE-ing the resource this instance's
	/// href points at would remove the master and every other override sharing it, since a
	/// recurrence-override instance's "resource" <i>is</i> the whole series (§1). A cancelled
	/// <c>VEVENT</c> (RFC 5545 §3.8.1.11) is the correct representation for "this occurrence no
	/// longer happens" without disturbing the recurrence rule itself.
	/// </summary>
	private async Task CancelOverrideAsync(Account account, CalendarEvent ev, Uri target, CancellationToken ct)
	{
		var getRequest = await requests.CreateAsync(account, HttpMethod.Get, target, ct);
		using var getResponse = await SendAsync(getRequest, ct);
		if (getResponse.StatusCode == HttpStatusCode.NotFound)
		{
			// The series is already gone — there is nothing left to cancel an instance of.
			return;
		}
		getResponse.EnsureSuccessStatusCode();
		var currentIcs = await getResponse.Content.ReadAsStringAsync(ct);

		var cancelled = ToDto(ev) with { Status = EventStatus.Cancelled };
		var merged = CalDavIcs.MergeOverride(currentIcs, ev.ICalUid, cancelled);

		var putRequest = await requests.CreateAsync(account, HttpMethod.Put, target, ct);
		CalDavWebDavRequest.SetIfMatch(putRequest, ev.ProviderRevision);
		putRequest.Content = new StringContent(merged, System.Text.Encoding.UTF8, "text/calendar");

		using var putResponse = await SendAsync(putRequest, ct);
		if (putResponse.StatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The CalDAV event changed on the server since it was last read.");
		}
		putResponse.EnsureSuccessStatusCode();
	}

	/// <summary>
	/// RSVP over CalDAV/IMAP: an iTIP <c>REPLY</c> — one <c>VEVENT</c> naming only the replying
	/// attendee (RFC 5546 §3.2.3) — sent to the organiser as a `text/calendar; method=REPLY`
	/// attachment on an ordinary email, through the same <see cref="IMailProvider.SendAsync"/>
	/// this account already sends mail with. Nothing calendar-specific about delivery: the
	/// organiser's calendar client is what interprets the attachment.
	/// </summary>
	public Task RespondToInviteAsync(
		Account account,
		CalendarEvent ev,
		InviteResponse response,
		string? comment,
		Address replyingAs,
		CancellationToken ct
	) => new ItipReplySender(mail).SendAsync(account, ev, response, comment, replyingAs, ct);

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
