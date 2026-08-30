namespace MyloMail.Api.Domain;

public class Calendar
{
	public Guid Id { get; set; }
	public Guid AccountId { get; set; }
	public string ProviderCalendarId { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
	public string? Colour { get; set; }
	public bool IsDefault { get; set; }
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

	/// <summary>iCalendar <c>RECURRENCE-ID</c> — which occurrence this overrides.</summary>
	public DateTimeOffset? RecurrenceId { get; set; }

	/// <summary>Set on precondition failure. Both versions are retained for the user to resolve (§15).</summary>
	public bool SyncConflict { get; set; }
}

public record Attendee(string? Name, string Email, AttendeeRole Role, ResponseStatus ResponseStatus);

public enum AttendeeRole
{
	Required,
	Optional,
	Resource,
}

public enum ResponseStatus
{
	NeedsAction,
	Accepted,
	Declined,
	Tentative,
}

public enum EventStatus
{
	Confirmed,
	Tentative,
	Cancelled,
}

public enum InviteResponse
{
	Accept,
	Decline,
	Tentative,
}
