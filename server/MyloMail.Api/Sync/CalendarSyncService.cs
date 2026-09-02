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
	/// <param name="resolvingEventId">
	/// Null for every ordinary sync pass — routine polling must never silently pick a side on
	/// a flagged conflict, so a conflicted event's upsert is skipped, leaving the local edit
	/// and the flag both intact until the user explicitly resolves it. Set only by
	/// <see cref="CalendarEventService.ResolveConflictAsync"/>'s "keep theirs" path, naming
	/// the one event the user has just explicitly chosen to overwrite with the server's
	/// version — every other conflicted event this sync happens to also touch is still left
	/// alone (§15).
	/// </param>
	public async Task SynchronizeAsync(
		Account account,
		Guid? resolvingEventId = null,
		CancellationToken ct = default
	)
	{
		var provider = providers.For(account);
		// CalDAV hands back a fresh HttpClient per resolution (§15 — certificate trust is a
		// per-account decision), so it is this call's to release, not the app's to pool.
		using var disposable = provider as IDisposable;

		var observed = await provider.ListCalendarsAsync(account, ct);
		var removedEventIds = await ReconcileCalendarsAsync(account.Id, observed, ct);
		foreach (var eventId in removedEventIds)
		{
			await events.CalendarEventUpdatedAsync(eventId);
		}

		// A local-only calendar has no provider backing at all (§1, §13 Epic 7) — nothing to
		// ask `provider.SyncCalendarAsync` for, and no cursor for it to ever advance.
		var calendarIds = await context
			.Calendars.Where(c => c.AccountId == account.Id && !c.IsLocalOnly)
			.Select(c => c.Id)
			.ToListAsync(ct);
		foreach (var calendarId in calendarIds)
		{
			await SynchronizeCalendarAsync(account, calendarId, provider, resolvingEventId, ct);
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
			.Calendars.Where(c =>
				c.AccountId == accountId && !c.IsLocalOnly && !observedIds.Contains(c.ProviderCalendarId)
			)
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
		Guid? resolvingEventId,
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
			await ApplyPageAsync(account.Id, calendarId, page, commitCursor: !page.HasMore, resolvingEventId, ct);
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
		Guid accountId,
		Guid calendarId,
		CalendarSyncResult page,
		bool commitCursor,
		Guid? resolvingEventId,
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
				// A provider reports one resource deleted, not one event: CalDAV's master and
				// its overrides share an href, so a deleted master takes its overrides with it
				// rather than leaving them as orphaned standalone events (§1 — recurrence is a
				// set, and deleting the set deletes all of it).
				var children = await context.CalendarEvents.Where(e => e.RecurrenceMasterId == existing.Id).ToListAsync(ct);
				context.CalendarEvents.RemoveRange(children);
				changed.AddRange(children.Select(c => c.Id));
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

			// A mailed invite (§13 Epic 7) may already have materialised this same event, by
			// UID, under the account's local-only pseudo-calendar before this account ever had
			// a real calendar synced against it. Adopted here rather than left to become a
			// duplicate: the mail-materialised row keeps its id (and so keeps working as the
			// target of any RSVP already sent against it) but moves onto the real calendar and
			// gains a real provider identity, same as any other upsert from here on.
			if (existing is null)
			{
				var localCalendarIds = await context
					.Calendars.Where(c => c.AccountId == accountId && c.IsLocalOnly)
					.Select(c => c.Id)
					.ToListAsync(ct);
				var materialised = await context.CalendarEvents.SingleOrDefaultAsync(
					e => e.ICalUid == dto.ICalUid && localCalendarIds.Contains(e.CalendarId),
					ct
				);
				if (materialised is not null)
				{
					materialised.CalendarId = calendarId;
					existing = materialised;
				}
			}

			// A still-unresolved conflict is left alone during ordinary sync — the local edit
			// and the flag both survive, exactly as they did the moment the conflict was
			// detected, until the user explicitly resolves it (§15). The one exception is the
			// event named by `resolvingEventId`: that is this call's whole reason for
			// running, from `ResolveConflictAsync`'s "keep theirs" path, and applying the
			// server's version to it is the entire point.
			if (existing is { SyncConflict: true } && existing.Id != resolvingEventId)
			{
				continue;
			}

			var ev = existing ?? new CalendarEvent { Id = Guid.NewGuid(), CalendarId = calendarId };
			Apply(ev, dto);
			if (existing is null)
			{
				context.CalendarEvents.Add(ev);
			}
			changed.Add(ev.Id);
		}

		// Flushed before the recurrence-resolution queries below: those run as ordinary
		// SingleAsync lookups against the database, which cannot see an Add()ed entity that
		// has not been saved yet — including one added earlier in this same page.
		await context.SaveChangesAsync(ct);

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

		// The server's own current copy just landed on top of whatever was here — by
		// definition nothing is unresolved about that any more, whether this upsert came from
		// ordinary polling or a user's explicit "keep theirs" conflict resolution (§15).
		// Without this, a conflict flagged during a local edit stayed stuck true forever once
		// the next routine sync pass silently pulled the same server version over it anyway.
		target.SyncConflict = false;
	}
}
