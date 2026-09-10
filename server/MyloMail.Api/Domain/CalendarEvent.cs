using Tapper;

namespace MyloMail.Api.Domain;

public class Calendar
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public string ProviderCalendarId { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string? Colour { get; set; }
	public bool IsDefault { get; set; }

	/// <summary>
	/// Opaque CalDAV sync token for this collection. It advances only in the same transaction
	/// as the event page it covers; replaying a page is safe, skipping one is not.
	/// </summary>
	public string? SyncCursor { get; set; }

	/// <summary>
	/// No provider backing at all — holds events materialised from a mailed invite on an
	/// account with no configured calendar (§1, §13 Epic 7). Never touched by
	/// <c>CalendarSyncService.ReconcileCalendarsAsync</c>'s remove-what-the-provider-no-longer-
	/// reports logic, since no provider has ever reported it. RSVP against an event here still
	/// works: it sends an iTIP <c>REPLY</c> as mail via the account's own send path, the same
	/// mechanism CalDAV/IMAP accounts already use — no calendar-provider API call is needed.
	/// </summary>
	public bool IsLocalOnly { get; set; }
}

public class CalendarEvent
{
	public Guid Id { get; set; }
	public Guid CalendarId { get; set; }
	public string ProviderEventId { get; set; } = string.Empty;

	/// <summary>The iCalendar <c>UID</c> — the cross-system identity used to match invites against events.</summary>
	public string ICalUid { get; set; } = string.Empty;

	/// <summary>ETag or <c>@odata.etag</c>. Drives detect-don't-merge conflict handling (§15).</summary>
	public string? ProviderRevision { get; set; }

	/// <summary>iTIP <c>SEQUENCE</c>, for invite update ordering.</summary>
	public int Sequence { get; set; }

	public string Title { get; set; } = string.Empty;
	public string? Location { get; set; }
	public string? Description { get; set; }

	public DateTimeOffset Start { get; set; }
	public DateTimeOffset End { get; set; }

	/// <summary>Start and end zones are separate because they legitimately differ (§1).</summary>
	public string? StartTimeZoneId { get; set; }
	public string? EndTimeZoneId { get; set; }

	public bool IsAllDay { get; set; }

	public Address? Organizer { get; set; }
	public IReadOnlyList<Attendee> Attendees { get; set; } = [];
	public EventStatus Status { get; set; }
	public IReadOnlyList<DateTimeOffset> Reminders { get; set; } = [];

	/// <summary>
	/// Recurrence is a set, not a single rule: iCalendar recurrence comprises <c>RRULE</c>
	/// plus <c>RDATE</c> and <c>EXDATE</c>, with modified and cancelled instances existing as
	/// separate objects. Normalising to one rule string would lose information (§1).
	/// </summary>
	public IReadOnlyList<string> RecurrenceRules { get; set; } = [];
	public IReadOnlyList<DateTimeOffset> RecurrenceDates { get; set; } = [];
	public IReadOnlyList<DateTimeOffset> ExceptionDates { get; set; } = [];

	/// <summary>Null on the master; set on modified and cancelled instances.</summary>
	public Guid? RecurrenceMasterId { get; set; }

	/// <summary>
	/// The provider-side master reference is retained solely to resolve the local canonical
	/// relationship when instances and their master arrive on different sync pages.
	/// </summary>
	public string? RecurrenceMasterProviderEventId { get; set; }

	/// <summary>iCalendar <c>RECURRENCE-ID</c> — which occurrence this overrides.</summary>
	public DateTimeOffset? RecurrenceId { get; set; }

	/// <summary>Set on precondition failure. Both versions are retained for the user to resolve (§15).</summary>
	public bool SyncConflict { get; set; }
}

/// <summary>
/// The durable local intent for a calendar event whose initial provider creation has not yet
/// been conclusively materialised. It keeps the canonical <see cref="CalendarEvent"/> free of
/// invented provider identity while carrying a stable UID for ambiguity reconciliation (§6).
/// </summary>
public class CalendarCreationAttempt
{
	public Guid Id { get; set; }
	/// <summary>
	/// Provider-safe idempotency/recovery key. It is distinct from the RFC 5545 UID because
	/// Google and Graph do not permit clients to set that server-owned field.
	/// </summary>
	public string ProviderCreationKey { get; set; } = string.Empty;
	public Guid CalendarId { get; set; }
	public string ICalUid { get; set; } = string.Empty;
	public string Title { get; set; } = string.Empty;
	public string? Location { get; set; }
	public string? Description { get; set; }
	public DateTimeOffset Start { get; set; }
	public DateTimeOffset End { get; set; }
	public bool IsAllDay { get; set; }

	/// <summary>
	/// Committed immediately before the irreversible provider call. Its existence is evidence
	/// only that the remote outcome may be ambiguous; empty recovery is never a replay permit.
	/// </summary>
	public DateTimeOffset DispatchedAt { get; set; }
}

public record Attendee(
	string? Name,
	string Email,
	AttendeeRole Role,
	ResponseStatus ResponseStatus,
	DateTimeOffset? ResponseTimestamp = null
);

[TranspilationSource]
public enum AttendeeRole
{
	Required,
	Optional,
	Resource,
}

[TranspilationSource]
public enum ResponseStatus
{
	NeedsAction,
	Accepted,
	Declined,
	Tentative,
}

[TranspilationSource]
public enum EventStatus
{
	Confirmed,
	Tentative,
	Cancelled,
}

[TranspilationSource]
public enum InviteResponse
{
	Accept,
	Decline,
	Tentative,
}
