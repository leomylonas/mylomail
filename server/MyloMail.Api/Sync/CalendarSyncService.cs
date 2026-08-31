using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// Materialises calendar topology and incremental pages into the sole local calendar model.
/// </summary>
/// <remarks>
/// A provider continuation is deliberately held only in this invocation. Each page is an
/// idempotent upsert transaction, but a collection token moves only after the final page: a
/// crash replays changes safely and can never skip a page (§3).
/// </remarks>
public sealed class CalendarSyncService(
	MyloMailDbContext context,
	ICalendarProviderFactory providers,
	IHubEvents events,
	IFaultInjector faults
)
{
	public async Task SynchronizeAsync(Account account, CancellationToken ct = default)
	{
		var provider = providers.For(account);
		var observed = await provider.ListCalendarsAsync(account, ct);
		var removedEventIds = await ReconcileCalendarsAsync(account.Id, observed, ct);
		foreach (var eventId in removedEventIds)
		{
			await events.CalendarEventUpdatedAsync(eventId);
		}

		var calendarIds = await context
			.Calendars.Where(c => c.AccountId == account.Id)
			.Select(c => c.Id)
			.ToListAsync(ct);
		foreach (var calendarId in calendarIds)
		{
			await SynchronizeCalendarAsync(account, calendarId, provider, ct);
		}
	}

	private async Task<IReadOnlyList<Guid>> ReconcileCalendarsAsync(
		Guid accountId,
		IReadOnlyList<CalendarDto> observed,
		CancellationToken ct
	)
	{
		foreach (var dto in observed)
		{
			var calendar = await context.Calendars.SingleOrDefaultAsync(
				c => c.AccountId == accountId && c.ProviderCalendarId == dto.ProviderCalendarId,
				ct
			);
			if (calendar is null)
			{
				context.Calendars.Add(
					new Calendar
					{
						Id = Guid.NewGuid(),
						AccountId = accountId,
						ProviderCalendarId = dto.ProviderCalendarId,
						Name = dto.Name,
						Colour = dto.Colour,
						IsDefault = dto.IsDefault,
					}
				);
			}
			else
			{
				calendar.Name = dto.Name;
				calendar.Colour = dto.Colour;
				calendar.IsDefault = dto.IsDefault;
			}
		}

		var observedIds = observed.Select(c => c.ProviderCalendarId).ToHashSet(StringComparer.Ordinal);
		var removed = await context
			.Calendars.Where(c => c.AccountId == accountId && !observedIds.Contains(c.ProviderCalendarId))
			.ToListAsync(ct);
		var removedIds = await context
			.CalendarEvents.Where(e => removed.Select(c => c.Id).Contains(e.CalendarId))
			.Select(e => e.Id)
			.ToListAsync(ct);
		context.Calendars.RemoveRange(removed);

		await context.SaveChangesAsync(ct);
		return removedIds;
	}

	private async Task SynchronizeCalendarAsync(
		Account account,
		Guid calendarId,
		ICalendarProvider provider,
		CancellationToken ct
	)
	{
		var calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
		var cursor = calendar.SyncCursor;
		string? continuation = null;
		var resetForInvalidCursor = false;
		while (true)
		{
			CalendarSyncResult page;
			try
			{
				page = await provider.SyncCalendarAsync(account, calendar, cursor, continuation, ct);
			}
			catch (ProviderCursorInvalidException) when (!resetForInvalidCursor)
			{
				await ResetBaselineAsync(calendarId, ct);
				cursor = null;
				continuation = null;
				resetForInvalidCursor = true;
				continue;
			}
			await ApplyPageAsync(calendarId, page, commitCursor: !page.HasMore, ct);
			continuation = page.Continuation;
			if (continuation is null)
			{
				return;
			}
		}
	}

	/// <summary>
	/// A rejected sync token cannot safely be advanced. Discard the stale local baseline before
	/// requesting a fresh one, so the next successful final page owns both rows and its token.
	/// </summary>
	private async Task ResetBaselineAsync(Guid calendarId, CancellationToken ct)
	{
		var removedIds = await context.CalendarEvents.Where(e => e.CalendarId == calendarId).Select(e => e.Id).ToListAsync(ct);
		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		context.CalendarEvents.RemoveRange(await context.CalendarEvents.Where(e => e.CalendarId == calendarId).ToListAsync(ct));
		var calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
		calendar.SyncCursor = null;
		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		foreach (var eventId in removedIds)
		{
			await events.CalendarEventUpdatedAsync(eventId);
		}
	}

	private async Task ApplyPageAsync(
		Guid calendarId,
		CalendarSyncResult page,
		bool commitCursor,
		CancellationToken ct
	)
	{
		if (commitCursor && string.IsNullOrEmpty(page.NewCursor))
		{
			throw new InvalidOperationException("A completed calendar sync page must carry a provider cursor.");
		}
		faults.Reached(FaultPoints.SyncPageBeforeCommit);

		var changed = new List<Guid>();
		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		foreach (var deletedProviderEventId in page.DeletedProviderEventIds)
		{
			var existing = await context.CalendarEvents.SingleOrDefaultAsync(
				e => e.CalendarId == calendarId && e.ProviderEventId == deletedProviderEventId,
				ct
			);
			if (existing is not null)
			{
				var children = await context.CalendarEvents.Where(e => e.RecurrenceMasterId == existing.Id).ToListAsync(ct);
				foreach (var child in children)
				{
					child.RecurrenceMasterId = null;
					changed.Add(child.Id);
				}
				changed.Add(existing.Id);
				context.CalendarEvents.Remove(existing);
			}
		}

		foreach (var dto in page.Upserted)
		{
			if (string.IsNullOrEmpty(dto.ProviderRevision))
			{
				throw new InvalidOperationException("Calendar observations must carry a provider revision.");
			}

			var existing = await context.CalendarEvents.SingleOrDefaultAsync(
				e => e.CalendarId == calendarId && e.ProviderEventId == dto.ProviderEventId,
				ct
			);
			var ev = existing ?? new CalendarEvent { Id = Guid.NewGuid(), CalendarId = calendarId };
			Apply(ev, dto);
			if (existing is null)
			{
				context.CalendarEvents.Add(ev);
			}
			changed.Add(ev.Id);
		}

		// A recurrence relation refers to the local canonical event id, while providers carry
		// the master's volatile id. Resolve it inside this transaction after all page upserts,
		// so either page order is safe and no provider id leaks into persisted intent.
		foreach (var dto in page.Upserted)
		{
			var occurrence = await context.CalendarEvents.SingleAsync(
				e => e.CalendarId == calendarId && e.ProviderEventId == dto.ProviderEventId,
				ct
			);
			var master = await context.CalendarEvents.SingleOrDefaultAsync(
				e => e.CalendarId == calendarId && e.ProviderEventId == occurrence.RecurrenceMasterProviderEventId,
				ct
			);
			occurrence.RecurrenceMasterId = master?.Id;
		}
		foreach (var master in page.Upserted.Where(e => e.RecurrenceMasterProviderEventId is null))
		{
			var localMaster = await context.CalendarEvents.SingleAsync(
				e => e.CalendarId == calendarId && e.ProviderEventId == master.ProviderEventId,
				ct
			);
			var waitingChildren = await context
				.CalendarEvents.Where(e =>
					e.CalendarId == calendarId && e.RecurrenceMasterProviderEventId == master.ProviderEventId
				)
				.ToListAsync(ct);
			foreach (var child in waitingChildren)
			{
				child.RecurrenceMasterId = localMaster.Id;
				changed.Add(child.Id);
			}
		}

		if (commitCursor)
		{
			var calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
			calendar.SyncCursor = page.NewCursor;
		}

		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		foreach (var eventId in changed)
		{
			await events.CalendarEventUpdatedAsync(eventId);
		}
		faults.Reached(FaultPoints.SyncPageAfterCommit);
	}

	private static void Apply(CalendarEvent target, CalendarEventDto source)
	{
		target.ProviderEventId = source.ProviderEventId;
		target.ICalUid = source.ICalUid;
		target.ProviderRevision = source.ProviderRevision;
		target.Sequence = source.Sequence;
		target.Title = source.Title;
		target.Location = source.Location;
		target.Description = source.Description;
		target.Start = source.Start;
		target.End = source.End;
		target.StartTimeZoneId = source.StartTimeZoneId;
		target.EndTimeZoneId = source.EndTimeZoneId;
		target.IsAllDay = source.IsAllDay;
		target.Organizer = source.Organizer;
		target.Attendees = source.Attendees;
		target.Status = source.Status;
		target.Reminders = source.Reminders;
		target.RecurrenceRules = source.RecurrenceRules;
		target.RecurrenceDates = source.RecurrenceDates;
		target.ExceptionDates = source.ExceptionDates;
		target.RecurrenceId = source.RecurrenceId;
		target.RecurrenceMasterProviderEventId = source.RecurrenceMasterProviderEventId;
	}
}
