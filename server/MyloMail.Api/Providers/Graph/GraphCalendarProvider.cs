using System.Globalization;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using DomainAttendee = MyloMail.Api.Domain.Attendee;
using DomainCalendar = MyloMail.Api.Domain.Calendar;
using DomainResponseStatus = MyloMail.Api.Domain.ResponseStatus;
using GraphDayOfWeek = Microsoft.Graph.Models.DayOfWeekObject;
using GraphEvent = Microsoft.Graph.Models.Event;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Microsoft Graph calendar provider. It uses the same immutable-id request pipeline as the
/// Graph mail provider: an event id is retained as the provider identity only because every
/// request asks Graph to return immutable ids.
/// </summary>
/// <remarks>
/// Calendar delta is deliberately a bounded <c>calendarView/delta</c> query. Graph calendar
/// delta requires a date range; each baseline captures the rolling window at its first request,
/// and Graph carries that range in its next/delta links for the rest of the walk. A delta link is
/// therefore the only durable cursor; next links are page continuations only.
/// </remarks>
public sealed class GraphCalendarProvider(GraphOAuthAuthenticator oauth) : ICalendarProvider
{
	private const int WindowMonths = 12;

	public ProviderType Type => ProviderType.Microsoft365;

	public async Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		string? continuation = null;
		var calendars = new List<CalendarDto>();
		do
		{
			var page = continuation is null
				? await ThrottleAwareAsync(() => client.Me.Calendars.GetAsync(
					configuration => configuration.QueryParameters.Select = ["id", "name", "color", "isDefaultCalendar"],
					ct
				))
				: await ThrottleAwareAsync(() => client.Me.Calendars.WithUrl(continuation).GetAsync(null, ct));
			calendars.AddRange((page?.Value ?? []).Where(calendar => calendar.Id is not null).Select(calendar => new CalendarDto(
				calendar.Id!,
				calendar.Name ?? string.Empty,
				calendar.Color?.ToString(),
				calendar.IsDefaultCalendar ?? false
			)));
			continuation = page?.OdataNextLink;
		}
		while (continuation is not null);

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
		var client = await ClientAsync(account, ct);
		var delta = client.Me.Calendars[CalendarId(calendar)].CalendarView.Delta;
		var url = continuation ?? cursor;

		try
		{
			var page = url is null
				? await ThrottleAwareAsync(() => delta.GetAsDeltaGetResponseAsync(
					configuration =>
					{
						var now = DateTimeOffset.UtcNow;
						configuration.QueryParameters.StartDateTime = now.AddMonths(-WindowMonths).ToString("O", CultureInfo.InvariantCulture);
						configuration.QueryParameters.EndDateTime = now.AddMonths(WindowMonths).ToString("O", CultureInfo.InvariantCulture);
					},
					ct
				))
				: await ThrottleAwareAsync(() => delta.WithUrl(url).GetAsDeltaGetResponseAsync(null, ct));

			var events = page?.Value ?? [];
			var masters = new Dictionary<string, GraphEvent>();
			foreach (var masterId in events
				.Where(ev => ev.SeriesMasterId is not null)
				.Select(ev => ev.SeriesMasterId!)
				.Distinct(StringComparer.Ordinal))
			{
				try
				{
					var master = await ThrottleAwareAsync(() => client.Me.Events[masterId].GetAsync(
						configuration => configuration.QueryParameters.Select = [
							"id", "@odata.etag", "transactionId", "iCalUId", "subject", "body", "location",
							"start", "end", "isAllDay", "isCancelled", "showAs", "attendees", "isReminderOn",
							"reminderMinutesBeforeStart", "recurrence", "organizer", "responseStatus",
						],
						ct
					));
					if (master?.Id is not null)
					{
						masters[master.Id] = master;
					}
				}
				catch (ApiException ex) when (ex.ResponseStatusCode == 404)
				{
					// The delta page remains valid; Graph deleted its master after emitting an
					// occurrence. The next delta response will reconcile the deletion.
				}
			}
			var materialized = events.Concat(masters.Values).DistinctBy(ev => ev.Id).ToList();
			return new CalendarSyncResult(
				page?.OdataNextLink is null ? page?.OdataDeltaLink : null,
				page?.OdataNextLink,
				[.. materialized.Where(ev => ev.Id is not null && !IsRemoved(ev)).Select(ToDto)],
				[.. materialized.Where(ev => ev.Id is not null && IsRemoved(ev)).Select(ev => ev.Id!)]
			);
		}
		catch (ApiException ex) when (url is not null && ex.ResponseStatusCode is 400 or 404 or 410)
		{
			throw new ProviderCursorInvalidException("Graph calendar delta cursor has been invalidated.", ex);
		}
	}

	public async Task<CalendarEventCreation> CreateEventAsync(Account account, DomainCalendar calendar, CalendarEventDto ev, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		var toCreate = ToGraphEvent(ev);
		toCreate.TransactionId = ev.ProviderCreationKey;
		var created = await ThrottleAwareAsync(
			() => client.Me.Calendars[CalendarId(calendar)].Events.PostAsync(toCreate, cancellationToken: ct)
		) ?? throw new InvalidOperationException("Graph did not return the created event.");
		var revision = RevisionOf(created) ?? throw new InvalidOperationException("Graph did not return the created event's ETag.");
		return new CalendarEventCreation(
			created.Id ?? throw new InvalidOperationException("Graph did not return an immutable event id."),
			revision,
			created.ICalUId
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
		var client = await ClientAsync(account, ct);
		var request = client.Me.Calendars[CalendarId(calendar)].Events;
		string? continuation = null;
		while (true)
		{
			var page = continuation is null
				? await ThrottleAwareAsync(() => request.GetAsync(
					configuration =>
					{
						configuration.QueryParameters.Select = [
							"id", "@odata.etag", "transactionId", "iCalUId", "subject", "body", "location",
							"start", "end", "isAllDay", "isCancelled", "showAs", "attendees", "isReminderOn",
							"reminderMinutesBeforeStart", "recurrence", "organizer", "responseStatus", "seriesMasterId", "originalStart",
						];
						configuration.QueryParameters.Top = 100;
					},
					ct
				))
				: await ThrottleAwareAsync(() => request.WithUrl(continuation).GetAsync(null, ct));
			var found = page?.Value?.FirstOrDefault(eventItem =>
				string.Equals(eventItem.TransactionId, providerCreationKey, StringComparison.Ordinal)
				|| string.Equals(eventItem.ICalUId, stableICalUid, StringComparison.OrdinalIgnoreCase)
			);
			if (found?.Id is not null)
			{
				return ToDto(found);
			}
			continuation = page?.OdataNextLink;
			if (continuation is null)
			{
				break;
			}
		}
		return null;
	}
	public async Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		try
		{
			var updated = await ThrottleAwareAsync(() => client.Me.Events[ev.ProviderEventId].PatchAsync(
				ToGraphEvent(ToDto(ev)),
				configuration =>
				{
					if (expectedETag is not null)
					{
						configuration.Headers.Add("If-Match", expectedETag);
					}
				},
				ct
			));
			if (updated?.AdditionalData?.TryGetValue("@odata.etag", out var etag) == true && etag is string value)
			{
				ev.ProviderRevision = value;
			}
		}
		catch (ApiException ex) when (ex.ResponseStatusCode is 404 or 412)
		{
			throw new ProviderConflictException("This calendar event changed somewhere else since it was last read.");
		}
	}

	public async Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		try
		{
			await ThrottleAwareAsync(() => client.Me.Events[ev.ProviderEventId].DeleteAsync(
				configuration =>
				{
					if (ev.ProviderRevision is not null)
					{
						configuration.Headers.Add("If-Match", ev.ProviderRevision);
					}
				},
				ct
			));
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			// The requested end state already holds.
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 412)
		{
			throw new ProviderConflictException("This calendar event changed somewhere else since it was last read.");
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
		var client = await ClientAsync(account, ct);
		switch (response)
		{
			case InviteResponse.Accept:
				await ThrottleAwareAsync(() => client.Me.Events[ev.ProviderEventId].Accept.PostAsync(new() { Comment = comment, SendResponse = true }, cancellationToken: ct));
				break;
			case InviteResponse.Decline:
				await ThrottleAwareAsync(() => client.Me.Events[ev.ProviderEventId].Decline.PostAsync(new() { Comment = comment, SendResponse = true }, cancellationToken: ct));
				break;
			case InviteResponse.Tentative:
				await ThrottleAwareAsync(() => client.Me.Events[ev.ProviderEventId].TentativelyAccept.PostAsync(new() { Comment = comment, SendResponse = true }, cancellationToken: ct));
				break;
			default:
				throw new ArgumentOutOfRangeException(nameof(response), response, null);
		}
	}

	private async Task<GraphServiceClient> ClientAsync(Account account, CancellationToken ct)
	{
		try
		{
			await oauth.AcquireTokenAsync(account, ct);
			var credential = new GraphAccountTokenCredential(oauth, account);
			var authenticationProvider =
				GraphMailProvider.CreateAuthenticationProvider(credential);
			var http = GraphClientFactory.Create(
				authenticationProvider,
				[new GraphImmutableIdHandler()]
			);
			return new GraphServiceClient(http, authenticationProvider);
		}
		catch (MsalException ex) when (!GraphOAuthAuthenticator.IsAdminConsentRequired(ex))
		{
			throw new ProviderAuthenticationException(ex.Message, ex);
		}
	}

	internal static CalendarEventDto ToDto(GraphEvent ev)
	{
		var start = DateTimeOf(ev.Start, CanonicalTimeZoneId(ev.Start?.TimeZone));
		var end = DateTimeOf(ev.End, CanonicalTimeZoneId(ev.End?.TimeZone));
		var recurrence = RecurrenceOf(ev.Recurrence);
		return new CalendarEventDto
		{
			ProviderEventId = ev.Id ?? throw new InvalidOperationException("Graph event has no immutable id."),
			ICalUid = ev.ICalUId ?? ev.Id!,
			ProviderCreationKey = ev.TransactionId,
			ProviderRevision = RevisionOf(ev),
			// Microsoft Graph exposes ETag/change-key concurrency but no iTIP SEQUENCE value;
			// its native RSVP actions own ordering. Zero means unavailable, never invented.
			Sequence = 0,
			Title = ev.Subject ?? string.Empty,
			Location = ev.Location?.DisplayName,
			Description = ev.Body?.Content,
			Start = start,
			End = end,
			StartTimeZoneId = CanonicalTimeZoneId(ev.Start?.TimeZone),
			EndTimeZoneId = CanonicalTimeZoneId(ev.End?.TimeZone),
			IsAllDay = ev.IsAllDay ?? false,
			Organizer = AddressOf(ev.Organizer),
			Attendees = [.. (ev.Attendees ?? []).Where(attendee => attendee.EmailAddress?.Address is not null).Select(attendee => new DomainAttendee(attendee.EmailAddress!.Name, attendee.EmailAddress.Address!, RoleOf(attendee.Type), ResponseOf(attendee.Status?.Response)))],
			Status = ev.IsCancelled == true ? EventStatus.Cancelled : ev.ShowAs == FreeBusyStatus.Tentative ? EventStatus.Tentative : EventStatus.Confirmed,
			Reminders = ev.IsReminderOn == true && ev.ReminderMinutesBeforeStart is { } minutes ? [start.AddMinutes(-minutes)] : [],
			RecurrenceRules = recurrence,
			RecurrenceMasterProviderEventId = ev.SeriesMasterId,
			RecurrenceId = ev.OriginalStart,
		};
	}

	private static GraphEvent ToGraphEvent(CalendarEventDto ev) => new()
	{
		Subject = ev.Title,
		Body = ev.Description is null ? null : new ItemBody { ContentType = BodyType.Html, Content = ev.Description },
		Location = ev.Location is null ? null : new Location { DisplayName = ev.Location },
		Start = DateTimeTimeZoneOf(ev.Start, ev.StartTimeZoneId),
		End = DateTimeTimeZoneOf(ev.End, ev.EndTimeZoneId),
		IsAllDay = ev.IsAllDay,
		Attendees = [.. ev.Attendees.Select(attendee => new Microsoft.Graph.Models.Attendee { EmailAddress = new EmailAddress { Name = attendee.Name, Address = attendee.Email }, Type = TypeOf(attendee.Role) })],
		IsReminderOn = ev.Reminders.Count > 0,
		ReminderMinutesBeforeStart = ev.Reminders.Count > 0 ? Math.Max(0, (int)(ev.Start - ev.Reminders.Min()).TotalMinutes) : null,
		Recurrence = GraphRecurrenceOf(ev),
	};

	private static string CalendarId(DomainCalendar calendar) => string.IsNullOrEmpty(calendar.ProviderCalendarId)
		? throw new InvalidOperationException("Graph calendars always have a provider id.")
		: calendar.ProviderCalendarId;

	private static bool IsRemoved(GraphEvent ev) => ev.AdditionalData?.ContainsKey("@removed") == true;
	private static string? RevisionOf(GraphEvent ev) => ev.AdditionalData?.TryGetValue("@odata.etag", out var etag) == true ? etag as string : null;
	private static Address? AddressOf(Recipient? recipient) => recipient?.EmailAddress?.Address is string email ? new Address(recipient.EmailAddress.Name, email) : null;
	private static AttendeeRole RoleOf(AttendeeType? type) => type switch { AttendeeType.Optional => AttendeeRole.Optional, AttendeeType.Resource => AttendeeRole.Resource, _ => AttendeeRole.Required };
	private static DomainResponseStatus ResponseOf(ResponseType? response) => response switch { ResponseType.Accepted => DomainResponseStatus.Accepted, ResponseType.Declined => DomainResponseStatus.Declined, ResponseType.TentativelyAccepted => DomainResponseStatus.Tentative, _ => DomainResponseStatus.NeedsAction };
	private static AttendeeType TypeOf(AttendeeRole role) => role switch { AttendeeRole.Optional => AttendeeType.Optional, AttendeeRole.Resource => AttendeeType.Resource, _ => AttendeeType.Required };

	private static DateTimeOffset DateTimeOf(DateTimeTimeZone? value, string? canonicalTimeZoneId)
	{
		if (value?.DateTime is not { } dateTime)
		{
			return DateTimeOffset.MinValue;
		}
		if (DateTimeOffset.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offsetDateTime)
			&& (dateTime.EndsWith('Z') || dateTime.Contains('+', StringComparison.Ordinal) || dateTime.LastIndexOf('-') > 9))
		{
			return offsetDateTime;
		}

		var local = DateTime.SpecifyKind(DateTime.Parse(dateTime, CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
		var zone = TimeZoneInfo.FindSystemTimeZoneById(canonicalTimeZoneId ?? "Etc/UTC");
		return new DateTimeOffset(local, zone.GetUtcOffset(local));
	}

	private static DateTimeTimeZone DateTimeTimeZoneOf(DateTimeOffset value, string? timeZoneId)
	{
		var zone = TimeZoneInfo.FindSystemTimeZoneById(CanonicalTimeZoneId(timeZoneId) ?? "Etc/UTC");
		var local = TimeZoneInfo.ConvertTime(value, zone);
		return new DateTimeTimeZone
		{
			DateTime = local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
			TimeZone = GraphTimeZoneId(timeZoneId),
		};
	}

	private static string? CanonicalTimeZoneId(string? value) =>
		value is not null && TimeZoneInfo.TryConvertWindowsIdToIanaId(value, out var iana) ? iana : value;

	private static string GraphTimeZoneId(string? value) =>
		value is not null && TimeZoneInfo.TryConvertIanaIdToWindowsId(value, out var windows) ? windows : value ?? "UTC";

	private static IReadOnlyList<string> RecurrenceOf(PatternedRecurrence? recurrence)
	{
		if (recurrence?.Pattern is not { } pattern || recurrence.Range is not { } range || pattern.Type is null)
		{
			return [];
		}

		var fields = new List<string> { $"FREQ={pattern.Type.ToString()!.ToUpperInvariant().Replace("RELATIVE", string.Empty).Replace("ABSOLUTE", string.Empty)}", $"INTERVAL={pattern.Interval ?? 1}" };
		if (pattern.DaysOfWeek is { Count: > 0 }) fields.Add($"BYDAY={string.Join(',', pattern.DaysOfWeek.Select(day => day.ToString()![..2].ToUpperInvariant()))}");
		if (pattern.FirstDayOfWeek is not null) fields.Add($"WKST={pattern.FirstDayOfWeek.ToString()![..2].ToUpperInvariant()}");
		if (pattern.DayOfMonth is not null) fields.Add($"BYMONTHDAY={pattern.DayOfMonth}");
		if (pattern.Index is { } index) fields.Add($"BYSETPOS={PositionOf(index)}");
		if (pattern.Month is not null) fields.Add($"BYMONTH={pattern.Month}");
		if (range.NumberOfOccurrences is not null) fields.Add($"COUNT={range.NumberOfOccurrences}");
		if (range.EndDate is not null) fields.Add($"UNTIL={range.EndDate.Value:yyyyMMdd}T235959Z");
		return [string.Join(';', fields)];
	}

	private static PatternedRecurrence? GraphRecurrenceOf(CalendarEventDto ev)
	{
		if (ev.RecurrenceRules.Count == 0)
		{
			return null;
		}
		if (ev.RecurrenceRules.Count != 1 || ev.RecurrenceDates.Count != 0 || ev.ExceptionDates.Count != 0)
		{
			throw new NotSupportedException("Microsoft Graph recurrence supports one pattern rule, not RDATE or EXDATE sets.");
		}

		var fields = ev.RecurrenceRules[0]
			.Split(';', StringSplitOptions.RemoveEmptyEntries)
			.Select(field => field.Split('=', 2))
			.Where(field => field.Length == 2)
			.ToDictionary(field => field[0], field => field[1], StringComparer.OrdinalIgnoreCase);
		if (!fields.TryGetValue("FREQ", out var frequency))
		{
			throw new NotSupportedException("A Graph recurrence rule must contain FREQ.");
		}

		var hasByDay = fields.ContainsKey("BYDAY");
		var patternType = frequency.ToUpperInvariant() switch
		{
			"DAILY" => RecurrencePatternType.Daily,
			"WEEKLY" => RecurrencePatternType.Weekly,
			"MONTHLY" when hasByDay => RecurrencePatternType.RelativeMonthly,
			"MONTHLY" => RecurrencePatternType.AbsoluteMonthly,
			"YEARLY" when hasByDay => RecurrencePatternType.RelativeYearly,
			"YEARLY" => RecurrencePatternType.AbsoluteYearly,
			_ => throw new NotSupportedException($"Microsoft Graph does not support recurrence frequency '{frequency}'."),
		};
		var pattern = new RecurrencePattern
		{
			FirstDayOfWeek = fields.TryGetValue("WKST", out var weekStart) ? GraphDayOfWeekOf(weekStart) : null,
			Index = fields.TryGetValue("BYSETPOS", out var position) ? WeekIndexOf(position) : null,
			Type = patternType,
			Interval = fields.TryGetValue("INTERVAL", out var interval)
				? int.Parse(interval, CultureInfo.InvariantCulture)
				: 1,
			DayOfMonth = fields.TryGetValue("BYMONTHDAY", out var dayOfMonth) ? int.Parse(dayOfMonth, CultureInfo.InvariantCulture) : null,
			Month = fields.TryGetValue("BYMONTH", out var month) ? int.Parse(month, CultureInfo.InvariantCulture) : null,
			DaysOfWeek = fields.TryGetValue("BYDAY", out var byDay) ? [.. byDay.Split(',').Select(GraphDayOfWeekOf)] : null,
		};
		var range = new RecurrenceRange { StartDate = DateOnly.FromDateTime(ev.Start.DateTime) };
		if (fields.TryGetValue("COUNT", out var count))
		{
			range.Type = RecurrenceRangeType.Numbered;
			range.NumberOfOccurrences = int.Parse(count, CultureInfo.InvariantCulture);
		}
		else if (fields.TryGetValue("UNTIL", out var until))
		{
			range.Type = RecurrenceRangeType.EndDate;
			range.EndDate = DateOnly.FromDateTime(DateTimeOffset.Parse(until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime);
		}
		else
		{
			range.Type = RecurrenceRangeType.NoEnd;
		}
		return new PatternedRecurrence { Pattern = pattern, Range = range };
	}

	private static GraphDayOfWeek GraphDayOfWeekOf(string value) => value.ToUpperInvariant() switch
	{
		"MO" => GraphDayOfWeek.Monday,
		"TU" => GraphDayOfWeek.Tuesday,
		"WE" => GraphDayOfWeek.Wednesday,
		"TH" => GraphDayOfWeek.Thursday,
		"FR" => GraphDayOfWeek.Friday,
		"SA" => GraphDayOfWeek.Saturday,
		"SU" => GraphDayOfWeek.Sunday,
		_ => throw new NotSupportedException($"Microsoft Graph does not support recurrence day '{value}'."),
	};

	private static int PositionOf(WeekIndex index) => index switch
	{
		WeekIndex.First => 1,
		WeekIndex.Second => 2,
		WeekIndex.Third => 3,
		WeekIndex.Fourth => 4,
		WeekIndex.Last => -1,
		_ => throw new NotSupportedException($"Microsoft Graph recurrence index '{index}' is not representable as RRULE."),
	};

	private static WeekIndex WeekIndexOf(string value) => value switch
	{
		"1" => WeekIndex.First,
		"2" => WeekIndex.Second,
		"3" => WeekIndex.Third,
		"4" => WeekIndex.Fourth,
		"-1" => WeekIndex.Last,
		_ => throw new NotSupportedException($"Microsoft Graph does not support recurrence position '{value}'."),
	};

	private static CalendarEventDto ToDto(CalendarEvent ev) => new()
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
	};
}
