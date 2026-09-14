using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Providers.Gmail;
using MyloMail.Api.Providers.Graph;
using Xunit;
using DomainCalendar = MyloMail.Api.Domain.Calendar;

namespace MyloMail.Api.Tests.Providers;

[Trait("Category", "Conformance")]
[Trait("Category", "Deep")]
[Trait("Category", "LiveProvider")]
public abstract class CalendarProviderLiveTests
{
	protected abstract string? SkipReason { get; }
	protected abstract Task<(ICalendarProvider Provider, Account Account)> CreateSubjectAsync();

	[SkippableFact]
	public async Task An_event_can_be_created_found_synced_updated_conflicted_and_deleted()
	{
		Skip.If(SkipReason is not null, SkipReason ?? string.Empty);
		var (provider, account) = await CreateSubjectAsync();
		var calendar = await DefaultCalendarAsync(provider, account);
		var baseline = await SyncToEndAsync(provider, account, calendar, cursor: null);
		Assert.False(string.IsNullOrWhiteSpace(baseline.Cursor));

		var key = Guid.NewGuid().ToString("N");
		var start = DateTimeOffset.UtcNow.AddDays(7);
		start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond));
		var title = $"MyloMail calendar live {key}";
		var requested = new CalendarEventDto
		{
			ProviderEventId = string.Empty,
			ICalUid = $"{key}@mylomail.invalid",
			ProviderCreationKey = key,
			Title = title,
			Location = "MyloMail live fixture",
			Description = "Disposable provider conformance event",
			Start = start,
			End = start.AddHours(1),
			StartTimeZoneId = "Etc/UTC",
			EndTimeZoneId = "Etc/UTC",
			Status = EventStatus.Confirmed,
			Reminders = [start.AddMinutes(-15)],
		};

		CalendarEvent? cleanup = null;
		try
		{
			var creation = await provider.CreateEventAsync(
				account,
				calendar,
				requested,
				CancellationToken.None
			);
			Assert.False(string.IsNullOrWhiteSpace(creation.ProviderEventId));
			Assert.False(string.IsNullOrWhiteSpace(creation.ProviderRevision));
			cleanup = Editable(requested, calendar.Id, creation.ProviderEventId, creation.ProviderRevision);

			var found = await provider.FindEventAsync(
				account,
				calendar,
				requested.ICalUid,
				key,
				CancellationToken.None
			);
			Assert.NotNull(found);
			Assert.Equal(creation.ProviderEventId, found.ProviderEventId);
			Assert.Equal(title, found.Title);
			Assert.False(string.IsNullOrWhiteSpace(found.ProviderRevision));

			var createdDelta = await WaitForDeltaAsync(
				provider,
				account,
				calendar,
				baseline.Cursor,
				snapshot => snapshot.Upserted.Any(ev => ev.ProviderEventId == creation.ProviderEventId),
				"created event"
			);
			var createdEvent = Assert.Single(
				createdDelta.Upserted,
				ev => ev.ProviderEventId == creation.ProviderEventId
			);

			var current = await WaitForStableEventAsync(
				provider,
				account,
				calendar,
				requested.ICalUid,
				key
			);
			var staleRevision = current.ProviderRevision;
			Assert.False(string.IsNullOrWhiteSpace(staleRevision));
			var updatedTitle = $"{title} updated";
			var editable = Editable(
				current,
				calendar.Id,
				creation.ProviderEventId,
				current.ProviderRevision
			);
			editable.Title = updatedTitle;
			editable.Location = "MyloMail live fixture updated";
			editable.Sequence++;
			await provider.UpdateEventAsync(
				account,
				editable,
				staleRevision,
				CancellationToken.None
			);
			cleanup = editable;

			editable.Title = $"{title} stale overwrite";
			await Assert.ThrowsAsync<ProviderConflictException>(() =>
				provider.UpdateEventAsync(
					account,
					editable,
					staleRevision,
					CancellationToken.None
				)
			);

			var updatedDelta = await WaitForDeltaAsync(
				provider,
				account,
				calendar,
				createdDelta.Cursor,
				snapshot => snapshot.Upserted.Any(ev =>
					ev.ProviderEventId == creation.ProviderEventId && ev.Title == updatedTitle
				),
				"updated event"
			);
			var updatedEvent = Assert.Single(
				updatedDelta.Upserted,
				ev => ev.ProviderEventId == creation.ProviderEventId && ev.Title == updatedTitle
			);
			Assert.DoesNotContain(
				updatedDelta.Upserted,
				ev => ev.ProviderEventId == creation.ProviderEventId
					&& ev.Title.EndsWith("stale overwrite", StringComparison.Ordinal)
			);

			var toDelete = Editable(
				updatedEvent,
				calendar.Id,
				creation.ProviderEventId,
				updatedEvent.ProviderRevision
			);
			await provider.DeleteEventAsync(account, toDelete, CancellationToken.None);
			cleanup = null;

			await WaitForDeltaAsync(
				provider,
				account,
				calendar,
				updatedDelta.Cursor,
				snapshot => snapshot.DeletedProviderEventIds.Contains(
					creation.ProviderEventId,
					StringComparer.Ordinal
				),
				"deleted event"
			);
		}
		finally
		{
			if (cleanup is not null)
			{
				cleanup.ProviderRevision = null;
				await provider.DeleteEventAsync(account, cleanup, CancellationToken.None);
			}
		}
	}

	[SkippableFact]
	public async Task A_recurring_event_round_trips_through_provider_sync()
	{
		Skip.If(SkipReason is not null, SkipReason ?? string.Empty);
		var (provider, account) = await CreateSubjectAsync();
		var calendar = await DefaultCalendarAsync(provider, account);
		var baseline = await SyncToEndAsync(provider, account, calendar, cursor: null);
		Assert.False(string.IsNullOrWhiteSpace(baseline.Cursor));

		var key = Guid.NewGuid().ToString("N");
		var start = DateTimeOffset.UtcNow.AddDays(14);
		start = start.AddTicks(-(start.Ticks % TimeSpan.TicksPerSecond));
		var recurrenceRule =
			$"FREQ=WEEKLY;BYDAY={start.DayOfWeek.ToString()[..2].ToUpperInvariant()};UNTIL={start.AddDays(21):yyyyMMdd'T'HHmmss'Z'}";
		var requested = new CalendarEventDto
		{
			ProviderEventId = string.Empty,
			ICalUid = $"{key}@mylomail.invalid",
			ProviderCreationKey = key,
			Title = $"MyloMail recurring calendar live {key}",
			Start = start,
			End = start.AddHours(1),
			StartTimeZoneId = "America/New_York",
			EndTimeZoneId = "America/New_York",
			Status = EventStatus.Confirmed,
			RecurrenceRules = [recurrenceRule],
		};

		CalendarEvent? cleanup = null;
		try
		{
			var creation = await provider.CreateEventAsync(
				account,
				calendar,
				requested,
				CancellationToken.None
			);
			cleanup = Editable(
				requested,
				calendar.Id,
				creation.ProviderEventId,
				creation.ProviderRevision
			);
			var createdDelta = await WaitForDeltaAsync(
				provider,
				account,
				calendar,
				baseline.Cursor,
				snapshot => snapshot.Upserted.Any(ev =>
					ev.ProviderEventId == creation.ProviderEventId
				),
				"created recurring event"
			);
			var master = Assert.Single(
				createdDelta.Upserted,
				ev => ev.ProviderEventId == creation.ProviderEventId
			);
			Assert.Contains(
				master.RecurrenceRules,
				rule => rule.Contains("FREQ=WEEKLY", StringComparison.Ordinal)
			);

			var toDelete = Editable(
				master,
				calendar.Id,
				creation.ProviderEventId,
				master.ProviderRevision
			);
			await provider.DeleteEventAsync(account, toDelete, CancellationToken.None);
			cleanup = null;
			await WaitForDeltaAsync(
				provider,
				account,
				calendar,
				createdDelta.Cursor,
				snapshot => snapshot.DeletedProviderEventIds.Contains(
					creation.ProviderEventId,
					StringComparer.Ordinal
				),
				"deleted recurring event"
			);
		}
		finally
		{
			if (cleanup is not null)
			{
				cleanup.ProviderRevision = null;
				await provider.DeleteEventAsync(account, cleanup, CancellationToken.None);
			}
		}
	}

	private static async Task<DomainCalendar> DefaultCalendarAsync(
		ICalendarProvider provider,
		Account account
	)
	{
		var calendars = await provider.ListCalendarsAsync(account, CancellationToken.None);
		Assert.NotEmpty(calendars);
		var selected = calendars.FirstOrDefault(calendar => calendar.IsDefault) ?? calendars[0];
		return new DomainCalendar
		{
			Id = Guid.NewGuid(),
			AccountId = account.Id,
			ProviderCalendarId = selected.ProviderCalendarId,
			Name = selected.Name,
			Colour = selected.Colour,
			IsDefault = selected.IsDefault,
		};
	}

	private static async Task<CalendarEventDto> WaitForStableEventAsync(
		ICalendarProvider provider,
		Account account,
		DomainCalendar calendar,
		string stableICalUid,
		string providerCreationKey
	)
	{
		CalendarEventDto? previous = null;
		var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
		do
		{
			var current = await provider.FindEventAsync(
				account,
				calendar,
				stableICalUid,
				providerCreationKey,
				CancellationToken.None
			);
			if (
				current?.ProviderRevision is { Length: > 0 }
				&& current.ProviderRevision == previous?.ProviderRevision
			)
			{
				return current;
			}
			previous = current;
			await Task.Delay(TimeSpan.FromSeconds(1));
		}
		while (DateTimeOffset.UtcNow < deadline);

		throw new TimeoutException("The provider event revision did not stabilize within one minute.");
	}

	private static CalendarEvent Editable(
		CalendarEventDto source,
		Guid calendarId,
		string providerEventId,
		string? revision
	) => new()
	{
		Id = Guid.NewGuid(),
		CalendarId = calendarId,
		ProviderEventId = providerEventId,
		ICalUid = source.ICalUid,
		ProviderRevision = revision,
		Sequence = source.Sequence,
		Title = source.Title,
		Location = source.Location,
		Description = source.Description,
		Start = source.Start,
		End = source.End,
		StartTimeZoneId = source.StartTimeZoneId,
		EndTimeZoneId = source.EndTimeZoneId,
		IsAllDay = source.IsAllDay,
		Organizer = source.Organizer,
		Attendees = source.Attendees,
		Status = source.Status,
		Reminders = source.Reminders,
		RecurrenceRules = source.RecurrenceRules,
		RecurrenceDates = source.RecurrenceDates,
		ExceptionDates = source.ExceptionDates,
		RecurrenceMasterProviderEventId = source.RecurrenceMasterProviderEventId,
		RecurrenceId = source.RecurrenceId,
	};

	private static async Task<CalendarSyncSnapshot> WaitForDeltaAsync(
		ICalendarProvider provider,
		Account account,
		DomainCalendar calendar,
		string? cursor,
		Func<CalendarSyncSnapshot, bool> completed,
		string operation
	)
	{
		var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
		do
		{
			var snapshot = await SyncToEndAsync(provider, account, calendar, cursor);
			if (completed(snapshot))
			{
				return snapshot;
			}
			await Task.Delay(TimeSpan.FromSeconds(2));
		}
		while (DateTimeOffset.UtcNow < deadline);

		throw new TimeoutException($"The provider did not surface the {operation} within one minute.");
	}

	private static async Task<CalendarSyncSnapshot> SyncToEndAsync(
		ICalendarProvider provider,
		Account account,
		DomainCalendar calendar,
		string? cursor
	)
	{
		var upserted = new List<CalendarEventDto>();
		var deleted = new List<string>();
		string? continuation = null;
		while (true)
		{
			var page = await provider.SyncCalendarAsync(
				account,
				calendar,
				cursor,
				continuation,
				CancellationToken.None
			);
			upserted.AddRange(page.Upserted);
			deleted.AddRange(page.DeletedProviderEventIds);
			continuation = page.Continuation;
			if (continuation is null)
			{
				return new CalendarSyncSnapshot(page.NewCursor, upserted, deleted);
			}
		}
	}

	private sealed record CalendarSyncSnapshot(
		string? Cursor,
		IReadOnlyList<CalendarEventDto> Upserted,
		IReadOnlyList<string> DeletedProviderEventIds
	);
}

[Collection(LiveProviderTestCollections.Gmail)]
[Trait("Provider", "Gmail")]
[Trait("Category", "LiveProvider")]
[Trait("Area", "Calendar")]
public sealed class GoogleCalendarLiveTests : CalendarProviderLiveTests
{
	protected override string? SkipReason => ProviderLiveTestContext.GmailSkipReason;

	protected override async Task<(ICalendarProvider Provider, Account Account)> CreateSubjectAsync()
	{
		var context = await ProviderLiveTestContext.GmailAsync();
		return (new GoogleCalendarProvider(context.OAuth), context.Account);
	}
}

[Collection(LiveProviderTestCollections.Graph)]
[Trait("Provider", "Graph")]
[Trait("Category", "LiveProvider")]
[Trait("Area", "Calendar")]
public sealed class GraphCalendarLiveTests : CalendarProviderLiveTests
{
	protected override string? SkipReason => ProviderLiveTestContext.GraphSkipReason;

	protected override async Task<(ICalendarProvider Provider, Account Account)> CreateSubjectAsync()
	{
		var context = await ProviderLiveTestContext.GraphAsync();
		return (new GraphCalendarProvider(context.OAuth), context.Account);
	}




}
