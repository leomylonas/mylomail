using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.Contracts;

public record CalendarDto(string ProviderCalendarId, string Name, string? Colour, bool IsDefault);

/// <summary>
/// One page of calendar change. As with mail, a cursor is returned only once it covers
/// everything in this result and everything before it; <see cref="Continuation"/> resumes an
/// incomplete walk and is never persisted as sync position.
/// </summary>
public record CalendarSyncResult(
	string? NewCursor,
	string? Continuation,
	IReadOnlyList<CalendarEventDto> Upserted,
	IReadOnlyList<string> DeletedProviderEventIds
)
{
	public bool HasMore => Continuation is not null;
}

/// <summary>
/// An event as the provider reports it. Graph's <c>recurrence</c> object is translated into
/// the recurrence set on ingest; CalDAV supplies it natively (§1).
/// </summary>
public record CalendarEventDto
{
	public required string ProviderEventId { get; init; }
	public required string ICalUid { get; init; }
	public string? ProviderRevision { get; init; }
	public int Sequence { get; init; }

	public string Title { get; init; } = string.Empty;
	public string? Location { get; init; }
	public string? Description { get; init; }

	public required DateTimeOffset Start { get; init; }
	public required DateTimeOffset End { get; init; }
	public string? StartTimeZoneId { get; init; }
	public string? EndTimeZoneId { get; init; }
	public bool IsAllDay { get; init; }

	public Address? Organizer { get; init; }
	public IReadOnlyList<Attendee> Attendees { get; init; } = [];
	public EventStatus Status { get; init; }
	public IReadOnlyList<DateTimeOffset> Reminders { get; init; } = [];

	public IReadOnlyList<string> RecurrenceRules { get; init; } = [];
	public IReadOnlyList<DateTimeOffset> RecurrenceDates { get; init; } = [];
	public IReadOnlyList<DateTimeOffset> ExceptionDates { get; init; } = [];
	public string? RecurrenceMasterProviderEventId { get; init; }
	public DateTimeOffset? RecurrenceId { get; init; }
}
