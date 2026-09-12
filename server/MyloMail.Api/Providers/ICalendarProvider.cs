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
	/// <summary>
	/// True when one provider revision protects every row in an iCalendar recurrence set.
	/// CalDAV stores a master and its overrides in one resource; Graph and Google version
	/// their event objects independently.
	/// </summary>
	bool SharesRevisionAcrossRecurrenceSet => false;

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
	/// <summary>
	/// Performs deterministic provider-shape validation before any durable dispatch record or
	/// optimistic local edit is written. It must not perform network I/O.
	/// </summary>
	void ValidateEvent(CalendarEventDto ev) { }


	Task<CalendarEventCreation> CreateEventAsync(
		Account account,
		Calendar calendar,
		CalendarEventDto ev,
		CancellationToken ct
	);

	/// <summary>
	/// Locates an event whose provider creation may have succeeded before the local response
	/// committed. A null result remains ambiguous and must never authorize another create (§6).
	/// The RFC UID and provider-safe creation key are distinct because Google/Graph own iCalUId.
	/// </summary>
	Task<CalendarEventDto?> FindEventAsync(
		Account account,
		Calendar calendar,
		string stableICalUid,
		string providerCreationKey,
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
	/// <param name="replyingAs">
	/// The account's own address to reply from — deliberately not read off <paramref
	/// name="account"/> itself, since §1 keeps no <c>EmailAddress</c> column there (the
	/// authoritative address is the default <c>SendIdentity</c>, which lives in the database
	/// this provider layer does not query). The caller resolves it once and passes it down.
	/// </param>
	Task RespondToInviteAsync(
		Account account,
		CalendarEvent ev,
		InviteResponse response,
		string? comment,
		Address replyingAs,
		CancellationToken ct
	);
}

public interface ICalendarProviderFactory
{
	/// <summary>
	/// Resolves per account: CalDAV is configured on an IMAP account and its endpoint and
	/// credential slot are account-local, not properties of the IMAP provider type.
	/// </summary>
	ICalendarProvider For(Account account);
}
