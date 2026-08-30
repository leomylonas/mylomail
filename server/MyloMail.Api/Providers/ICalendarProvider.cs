using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers;

/// <summary>
/// Calendar access, parallel to <see cref="IMailProvider"/> (§2).
/// </summary>
/// <remarks>
/// Implementations: <c>GoogleCalendarProvider</c>, <c>GraphCalendarProvider</c>, and
/// <c>CalDavCalendarProvider</c>, which is genuinely generic and may be configured
/// independently against a plain IMAP account — an IMAP account has no calendar unless the
/// user supplies CalDAV details.
/// </remarks>
public interface ICalendarProvider
{
	ProviderType Type { get; }

	Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct);

	/// <exception cref="ProviderCursorInvalidException">
	/// The cursor can no longer be used and a baseline must be re-established (§3).
	/// </exception>
	Task<CalendarSyncResult> SyncCalendarAsync(
		Account account,
		Calendar calendar,
		string? cursor,
		string? continuation,
		CancellationToken ct
	);

	Task<string> CreateEventAsync(
		Account account,
		Calendar calendar,
		CalendarEventDto ev,
		CancellationToken ct
	);

	/// <summary>
	/// <paramref name="expectedETag"/> is passed through as an <c>If-Match</c> precondition
	/// where supported. A precondition failure surfaces as <c>ErrorCategory.Conflict</c>
	/// rather than an overwrite — detect, do not silently merge (§15).
	/// </summary>
	Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct);

	Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct);

	/// <summary>
	/// Graph and Google Calendar handle RSVP natively; the CalDAV/IMAP path generates an
	/// iCalendar <c>REPLY</c> and sends it via <see cref="IMailProvider.SendAsync"/>.
	/// </summary>
	Task RespondToInviteAsync(
		Account account,
		CalendarEvent ev,
		InviteResponse response,
		string? comment,
		CancellationToken ct
	);
}

public interface ICalendarProviderFactory
{
	ICalendarProvider For(ProviderType type);
}
