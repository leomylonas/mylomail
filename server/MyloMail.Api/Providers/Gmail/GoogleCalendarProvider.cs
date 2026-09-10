using System.Globalization;
using System.Net;
using Google;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using DomainCalendar = MyloMail.Api.Domain.Calendar;
using GoogleCalendarEvent = Google.Apis.Calendar.v3.Data.Event;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// Google's native Calendar API. Calendar event ids are encoded with their calendar because
/// <see cref="ICalendarProvider.UpdateEventAsync"/> receives an event but not its calendar.
/// Recurrence overrides additionally retain their series master and original start, which lets
/// mutations find the instance even when Google's ephemeral instance id is unavailable.
/// </summary>
public sealed class GoogleCalendarProvider(GmailOAuthAuthenticator oauth) : ICalendarProvider
{
	private const int SyncPageSize = 250;

	public ProviderType Type => ProviderType.Gmail;
	public async Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.CalendarList.List();
		var calendars = new List<CalendarDto>();
		do
		{
			var page = await request.ExecuteThrottleAwareAsync(ct);
			calendars.AddRange(
				(page.Items ?? [])
					.Where(item => item.Id is not null)
					.Select(item => new CalendarDto(
						item.Id!,
						item.Summary ?? item.Id!,
						item.BackgroundColor,
						item.Primary == true
					))
			);
			request.PageToken = page.NextPageToken;
		}
		while (request.PageToken is not null);

		return calendars;
	}

	public async Task<CalendarSyncResult> SyncCalendarAsync(
		Account account,
		DomainCalendar calendar,
		string? cursor,
		string? continuation,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.Events.List(calendar.ProviderCalendarId);
		request.SyncToken = cursor;
		request.PageToken = continuation;
		request.MaxResults = SyncPageSize;
		request.ShowDeleted = true;
		request.SingleEvents = false;

		try
		{
			var page = await request.ExecuteThrottleAwareAsync(ct);
			var upserted = new List<CalendarEventDto>();
			var deleted = new List<string>();
			foreach (var item in page.Items ?? [])
			{
				if (item.Id is null)
				{
					continue;
				}
				if (
					string.Equals(item.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
					&& (item.RecurringEventId is null || item.OriginalStartTime is null)
				)
				{
					deleted.Add(EncodeEventId(calendar.ProviderCalendarId, item));
					continue;
				}
				upserted.Add(ToDto(calendar.ProviderCalendarId, item));
			}

			return new CalendarSyncResult(
				page.NextPageToken is null ? page.NextSyncToken : null,
				page.NextPageToken,
				upserted,
				deleted
			);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
		{
			throw new ProviderCursorInvalidException("The Google Calendar sync token has expired.", ex);
		}
	}

	public async Task<CalendarEventCreation> CreateEventAsync(Account account, DomainCalendar calendar, CalendarEventDto ev, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		var toCreate = ToGoogleEvent(ev);
		toCreate.Id = ev.ProviderCreationKey;
		var created = await service.Events.Insert(toCreate, calendar.ProviderCalendarId).ExecuteThrottleAwareAsync(ct);
		if (created.Id is null)
		{
			throw new InvalidOperationException("Google Calendar created an event without an id.");
		}
		return new CalendarEventCreation(
			EncodeEventId(calendar.ProviderCalendarId, created),
			created.ETag ?? throw new InvalidOperationException("Google Calendar created an event without an ETag."),
			created.ICalUID
		);
	}
	public async Task<CalendarEventDto?> FindEventAsync(
		Account account,
		DomainCalendar calendar,
		string stableICalUid,
		string providerCreationKey,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		try
		{
			var found = await service.Events.Get(calendar.ProviderCalendarId, providerCreationKey).ExecuteThrottleAwareAsync(ct);
			// A cancelled Google resource is a tombstone, not a full event snapshot. It
			// proves no canonical event can be materialised; retain the durable attempt
			// conservatively rather than aborting the account's ordinary sync with ToDto.
			return found.Id is null || string.Equals(found.Status, "cancelled", StringComparison.OrdinalIgnoreCase)
				? null
				: ToDto(calendar.ProviderCalendarId, found);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		var reference = DecodeEventId(RequireProviderEventId(ev));
		var eventId = await ResolveEventIdAsync(service, reference, ct);
		var request = service.Events.Update(ToGoogleEvent(ev), reference.CalendarId, eventId);
		SetIfMatch(request, expectedETag);
		try
		{
			var updated = await request.ExecuteThrottleAwareAsync(ct);
			if (updated.ETag is { Length: > 0 } etag)
			{
				ev.ProviderRevision = etag;
			}
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The Google Calendar event changed on the server since it was last read.");
		}
	}

	public async Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		var reference = DecodeEventId(RequireProviderEventId(ev));
		var eventId = await ResolveEventIdAsync(service, reference, ct);
		var request = service.Events.Delete(reference.CalendarId, eventId);
		SetIfMatch(request, ev.ProviderRevision);
		try
		{
			await request.ExecuteThrottleAwareAsync(ct);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.PreconditionFailed)
		{
			throw new ProviderConflictException("The Google Calendar event changed on the server since it was last read.");
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
		{
			// The desired end state already holds.
		}
	}

	public async Task RespondToInviteAsync(
		Account account,
		CalendarEvent ev,
		InviteResponse response,
		string? comment,
		Address replyingAs,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var reference = DecodeEventId(RequireProviderEventId(ev));
		var eventId = await ResolveEventIdAsync(service, reference, ct);
		var request = service.Events.Patch(new GoogleCalendarEvent
		{
			AttendeesOmitted = true,
			Attendees =
			[
				new EventAttendee
				{
					Email = replyingAs.Email,
					ResponseStatus = ResponseOf(response),
					Comment = comment,
				},
			],
		}, reference.CalendarId, eventId);
		await request.ExecuteThrottleAwareAsync(ct);
	}

	internal static CalendarEventDto ToDto(string calendarId, GoogleCalendarEvent ev)
	{
		if (ev.Id is null)
		{
			throw new ArgumentException("Google Calendar event has no id.", nameof(ev));
		}
		var eventStart = ev.Start ?? ev.OriginalStartTime;
		var eventEnd = ev.End ?? eventStart;
		if (eventStart is null || eventEnd is null)
		{
			throw new ArgumentException("Google Calendar event has no start or end.", nameof(ev));
		}

		var recurrence = RecurrenceSet(ev.Recurrence);
		var start = DateTimeOf(eventStart);
		return new CalendarEventDto
		{
			ProviderEventId = EncodeEventId(calendarId, ev),
			ICalUid = ev.ICalUID ?? ev.Id,
			ProviderCreationKey = ev.Id,
			ProviderRevision = ev.ETag,
			Sequence = checked((int)(ev.Sequence ?? 0)),
			Title = ev.Summary ?? string.Empty,
			Location = ev.Location,
			Description = ev.Description,
			Start = start,
			End = DateTimeOf(eventEnd),
			StartTimeZoneId = eventStart.TimeZone,
			EndTimeZoneId = eventEnd.TimeZone,
			IsAllDay = eventStart.Date is not null,
			Organizer = ev.Organizer?.Email is { } organizer ? new Address(ev.Organizer.DisplayName, organizer) : null,
			Attendees = [.. (ev.Attendees ?? []).Where(a => a.Email is not null).Select(a => new Attendee(a.DisplayName, a.Email!, RoleOf(a), ResponseOf(a.ResponseStatus)))],
			Status = StatusOf(ev.Status),
			RecurrenceRules = recurrence.Rules,
			RecurrenceDates = recurrence.Dates,
			ExceptionDates = recurrence.Exceptions,
			Reminders = RemindersOf(ev, start),
			RecurrenceMasterProviderEventId = ev.RecurringEventId is null ? null : EncodeMasterId(calendarId, ev.RecurringEventId),
			RecurrenceId = ev.OriginalStartTime is null ? null : DateTimeOf(ev.OriginalStartTime),
		};
	}

	private async Task<CalendarService> ServiceAsync(Account account, CancellationToken ct)
	{
		try
		{
			var credential = await oauth.AuthorizeAsync(account, requireCalendarScope: true, ct);
			var service = new CalendarService(new BaseClientService.Initializer
			{
				HttpClientInitializer = credential,
				ApplicationName = "MyloMail",
			});
			service.AttachThrottleTracker(new GmailThrottleTracker());
			return service;
		}
		catch (TokenResponseException ex)
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}
	}

	private static async Task<string> ResolveEventIdAsync(CalendarService service, EventReference reference, CancellationToken ct)
	{
		if (reference.OriginalStart is null)
		{
			return reference.EventOrMasterId;
		}

		var request = service.Events.Instances(reference.CalendarId, reference.EventOrMasterId);
		request.ShowDeleted = true;
		string? pageToken = null;
		do
		{
			request.PageToken = pageToken;
			var page = await request.ExecuteThrottleAwareAsync(ct);
			var instance = (page.Items ?? []).FirstOrDefault(item =>
				item.Id is not null
				&& item.OriginalStartTime is not null
				&& DateTimeOf(item.OriginalStartTime) == reference.OriginalStart
			);
			if (instance?.Id is not null)
			{
				return instance.Id;
			}
			pageToken = page.NextPageToken;
		}
		while (pageToken is not null);

		throw new ProviderConflictException("The Google Calendar recurrence instance no longer exists.");
	}

	internal static GoogleCalendarEvent ToGoogleEvent(CalendarEventDto ev) => new()
	{
		Summary = ev.Title,
		Location = ev.Location,
		Description = ev.Description,
		Start = ToGoogleTime(ev.Start, ev.StartTimeZoneId, ev.IsAllDay),
		End = ToGoogleTime(ev.End, ev.EndTimeZoneId, ev.IsAllDay),
		Sequence = ev.Sequence,
		Status = ev.Status switch
		{
			EventStatus.Tentative => "tentative",
			EventStatus.Cancelled => "cancelled",
			_ => "confirmed",
		},
		Attendees = [.. ev.Attendees.Select(a => new EventAttendee
		{
			DisplayName = a.Name,
			Email = a.Email,
			Optional = a.Role == AttendeeRole.Optional ? true : null,
			Resource = a.Role == AttendeeRole.Resource ? true : null,
			ResponseStatus = ResponseOf(a.ResponseStatus),
		})],
		Reminders = RemindersOf(ev),
		Recurrence = RecurrenceOf(ev),
	};

	private static string RequireProviderEventId(CalendarEvent ev) =>
		ev.ProviderEventId ?? throw new InvalidOperationException("A pending calendar creation has no provider event id.");

	private static GoogleCalendarEvent ToGoogleEvent(CalendarEvent ev) => ToGoogleEvent(new CalendarEventDto
	{
		ProviderEventId = ev.ProviderEventId ?? string.Empty,
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
	});

	private static EventDateTime ToGoogleTime(DateTimeOffset value, string? timeZone, bool allDay) => allDay
		? new EventDateTime { Date = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
		: new EventDateTime { DateTimeDateTimeOffset = value, TimeZone = timeZone };

	internal static IList<string>? RecurrenceOf(CalendarEventDto ev)
	{
		var recurrence = new List<string>(ev.RecurrenceRules.Count + ev.RecurrenceDates.Count + ev.ExceptionDates.Count);
		recurrence.AddRange(ev.RecurrenceRules.Select(rule => $"RRULE:{rule}"));
		if (ev.RecurrenceDates.Count > 0)
		{
			recurrence.Add(RecurrenceLine("RDATE", ev.RecurrenceDates, ev));
		}
		if (ev.ExceptionDates.Count > 0)
		{
			recurrence.Add(RecurrenceLine("EXDATE", ev.ExceptionDates, ev));
		}
		return recurrence.Count == 0 ? null : recurrence;
	}

	private static string RecurrenceLine(string name, IReadOnlyList<DateTimeOffset> values, CalendarEventDto ev)
	{
		if (ev.IsAllDay)
		{
			return $"{name}:{string.Join(',', values.Select(value => value.ToString("yyyyMMdd", CultureInfo.InvariantCulture)))}";
		}
		if (ev.StartTimeZoneId is null)
		{
			return $"{name}:{string.Join(',', values.Select(RecurrenceDate))}";
		}

		var zone = TimeZoneInfo.FindSystemTimeZoneById(ev.StartTimeZoneId);
		return $"{name};TZID={ev.StartTimeZoneId}:{string.Join(',', values.Select(value => TimeZoneInfo.ConvertTime(value, zone).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)))}";
	}

	private static (IReadOnlyList<string> Rules, IReadOnlyList<DateTimeOffset> Dates, IReadOnlyList<DateTimeOffset> Exceptions) RecurrenceSet(IList<string>? recurrence)
	{
		var rules = new List<string>();
		var dates = new List<DateTimeOffset>();
		var exceptions = new List<DateTimeOffset>();
		foreach (var line in recurrence ?? [])
		{
			var separator = line.IndexOf(':');
			if (separator < 0)
			{
				continue;
			}
			var name = line[..separator];
			var values = line[(separator + 1)..];
			if (name.Equals("RRULE", StringComparison.OrdinalIgnoreCase))
			{
				rules.Add(values);
				continue;
			}

			var property = name.Split(';')[0];
			var timeZoneId = name
				.Split(';')
				.Skip(1)
				.Select(parameter => parameter.Split('=', 2))
				.FirstOrDefault(parameter => parameter.Length == 2 && parameter[0].Equals("TZID", StringComparison.OrdinalIgnoreCase))?
				.ElementAtOrDefault(1);
			if (property.Equals("RDATE", StringComparison.OrdinalIgnoreCase))
			{
				dates.AddRange(ParseRecurrenceDates(values, timeZoneId));
			}
			else if (property.Equals("EXDATE", StringComparison.OrdinalIgnoreCase))
			{
				exceptions.AddRange(ParseRecurrenceDates(values, timeZoneId));
			}
		}
		return (rules, dates, exceptions);
	}

	private static IEnumerable<DateTimeOffset> ParseRecurrenceDates(string values, string? timeZoneId)
	{
		foreach (var value in values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (value.EndsWith('Z')
				&& DateTimeOffset.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var utc))
			{
				yield return utc;
			}
			else if (DateTime.TryParseExact(value, ["yyyyMMdd'T'HHmmss", "yyyyMMdd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
			{
				var zone = timeZoneId is null ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
				yield return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));
			}
		}
	}

	private static IReadOnlyList<DateTimeOffset> RemindersOf(GoogleCalendarEvent ev, DateTimeOffset start) =>
		[
			.. (ev.Reminders?.Overrides ?? [])
				.Where(reminder => reminder.Minutes is { } minutes)
				.Select(reminder => start.AddMinutes(-reminder.Minutes!.Value)),
		];

	private static GoogleCalendarEvent.RemindersData? RemindersOf(CalendarEventDto ev)
	{
		if (ev.Reminders.Count == 0)
		{
			return null;
		}
		return new GoogleCalendarEvent.RemindersData
		{
			UseDefault = false,
			Overrides = [.. ev.Reminders
				.Where(reminder => reminder <= ev.Start)
				.Select(reminder => new EventReminder
				{
					Method = "popup",
					Minutes = checked((int)(ev.Start - reminder).TotalMinutes),
				})],
		};
	}

	private static DateTimeOffset DateTimeOf(EventDateTime value)
	{
		if (value.Date is { } date)
		{
			return new DateTimeOffset(DateOnly.Parse(date, CultureInfo.InvariantCulture).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
		}
		if (value.DateTimeDateTimeOffset is not { } dateTime)
		{
			throw new ArgumentException("Google Calendar event time has neither date nor dateTime.", nameof(value));
		}
		return dateTime;
	}

	private static string EncodeEventId(string calendarId, GoogleCalendarEvent ev) =>
		ev.RecurringEventId is { } master && ev.OriginalStartTime is { } original
			? Encode(calendarId, master, DateTimeOf(original))
			: Encode(calendarId, ev.Id!);

	private static string EncodeMasterId(string calendarId, string masterId) => Encode(calendarId, masterId);

	private static string Encode(string calendarId, string eventOrMasterId, DateTimeOffset? originalStart = null) =>
		$"gcal:{Base64(calendarId)}:{Base64(eventOrMasterId)}{(originalStart is null ? string.Empty : $":{originalStart.Value.UtcDateTime.Ticks}")}";

	private static EventReference DecodeEventId(string encoded)
	{
		var parts = encoded.Split(':');
		if (parts.Length is not (3 or 4) || parts[0] != "gcal")
		{
			throw new ArgumentException("The Google Calendar event id is malformed.", nameof(encoded));
		}
		return new EventReference(
			FromBase64(parts[1]),
			FromBase64(parts[2]),
			parts.Length == 4 && long.TryParse(parts[3], CultureInfo.InvariantCulture, out var ticks)
				? new DateTimeOffset(ticks, TimeSpan.Zero)
				: null
		);
	}

	private static string Base64(string value) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	private static string FromBase64(string value)
	{
		var padded = value.Replace('-', '+').Replace('_', '/');
		return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=')));
	}

	private static void SetIfMatch<TResponse>(Google.Apis.Requests.ClientServiceRequest<TResponse> request, string? eTag)
	{
		if (!string.IsNullOrEmpty(eTag))
		{
			request.ModifyRequest = message => message.Headers.TryAddWithoutValidation("If-Match", eTag);
		}
	}

	private static string RecurrenceDate(DateTimeOffset value) => value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
	private static EventStatus StatusOf(string? status) => status?.ToLowerInvariant() switch { "cancelled" => EventStatus.Cancelled, "tentative" => EventStatus.Tentative, _ => EventStatus.Confirmed };
	private static AttendeeRole RoleOf(EventAttendee attendee) => attendee.Resource == true ? AttendeeRole.Resource : attendee.Optional == true ? AttendeeRole.Optional : AttendeeRole.Required;
	private static ResponseStatus ResponseOf(string? status) => status?.ToLowerInvariant() switch { "accepted" => ResponseStatus.Accepted, "declined" => ResponseStatus.Declined, "tentative" => ResponseStatus.Tentative, _ => ResponseStatus.NeedsAction };
	private static string ResponseOf(ResponseStatus response) => response switch { ResponseStatus.Accepted => "accepted", ResponseStatus.Declined => "declined", ResponseStatus.Tentative => "tentative", _ => "needsAction" };
	private static string ResponseOf(InviteResponse response) => response switch { InviteResponse.Accept => "accepted", InviteResponse.Decline => "declined", _ => "tentative" };

	private sealed record EventReference(string CalendarId, string EventOrMasterId, DateTimeOffset? OriginalStart);
}
