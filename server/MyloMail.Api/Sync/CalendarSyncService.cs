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
	IFaultInjector faults,
	CalendarSyncGate gate
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
		using var lease = await gate.EnterAsync(account.Id, ct);
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
		await RecoverPendingCreationsAsync(account, provider, calendarIds, ct);
		foreach (var calendarId in calendarIds)
		{
			await SynchronizeCalendarAsync(account, calendarId, provider, resolvingEventId, ct);
		}
	}

	/// <summary>
	/// Reconciles ambiguous calendar creations without starting a normal poll. Startup uses
	/// this path even while periodic polling is paused: a durable dispatched create is
	/// outstanding work, not a polling preference.
	/// </summary>
	public async Task RecoverPendingCreationsOnlyAsync(Account account, CancellationToken ct = default)
	{
		using var lease = await gate.EnterAsync(account.Id, ct);
		var provider = providers.For(account);
		using var disposable = provider as IDisposable;
		var calendarIds = await (
			from attempt in context.CalendarCreationAttempts
			join calendar in context.Calendars on attempt.CalendarId equals calendar.Id
			where calendar.AccountId == account.Id && !calendar.IsLocalOnly
			select calendar.Id
		).Distinct().ToListAsync(ct);
		await RecoverPendingCreationsAsync(account, provider, calendarIds, ct);
	}

	/// <summary>
	/// Recovers initial provider creates whose local response commit was interrupted. A missing
	/// observation remains ambiguity, not permission to repeat creation (§6).
	/// </summary>
	private async Task RecoverPendingCreationsAsync(
		Account account,
		ICalendarProvider provider,
		IReadOnlyCollection<Guid> calendarIds,
		CancellationToken ct
	)
	{
		var pending = await (
			from attempt in context.CalendarCreationAttempts
			join calendar in context.Calendars on attempt.CalendarId equals calendar.Id
			where calendarIds.Contains(attempt.CalendarId)
			select new { Attempt = attempt, Calendar = calendar }
		).ToListAsync(ct);

		foreach (var item in pending)
		{
			var found = await provider.FindEventAsync(
				account,
				item.Calendar,
				item.Attempt.ICalUid,
				item.Attempt.ProviderCreationKey,
				ct
			);
			if (found is null)
			{
				continue;
			}
			if (string.IsNullOrEmpty(found.ProviderRevision))
			{
				throw new InvalidOperationException("Calendar creation recovery requires a provider revision.");
			}

			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			var claimed = await context.CalendarCreationAttempts.Where(a => a.Id == item.Attempt.Id).ExecuteDeleteAsync(ct);
			if (claimed == 0)
			{
				await transaction.RollbackAsync(ct);
				continue;
			}

			var existing = await context.CalendarEvents.SingleOrDefaultAsync(
				e => e.CalendarId == item.Attempt.CalendarId && e.ProviderEventId == found.ProviderEventId,
				ct
			);
			if (existing is null)
			{
				var materialised = new CalendarEvent { Id = item.Attempt.Id, CalendarId = item.Attempt.CalendarId };
				Apply(materialised, found);
				context.CalendarEvents.Add(materialised);
				await context.SaveChangesAsync(ct);
				await transaction.CommitAsync(ct);
				await events.CalendarEventUpdatedAsync(materialised.Id);
				continue;
			}

			await transaction.CommitAsync(ct);
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
		DateTimeOffset? rebaseWindowStart = null;
		DateTimeOffset? rebaseWindowEnd = null;
		HashSet<string>? rebaseObservedProviderIds = null;
		if (account.ProviderType == ProviderType.Microsoft365
			&& calendar.SyncCursor is not null
			&& (calendar.SyncWindowStartedAt is null
				|| calendar.SyncWindowStartedAt < DateTimeOffset.UtcNow.AddDays(-1)))
		{
			var now = DateTimeOffset.UtcNow;
			rebaseWindowStart = now.AddMonths(-12);
			rebaseWindowEnd = now.AddMonths(12);
			rebaseObservedProviderIds = new HashSet<string>(StringComparer.Ordinal);
			await ResetGraphWindowAsync(calendarId, ct);
			calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
		}
		var cursor = calendar.SyncCursor;
		string? continuation = null;
		var resetForInvalidCursor = false;
		while (true)
		{
			// Same §3 requirement as ChangeStreamService.SyncAsync/ReplayStagedAsync: this
			// call can span many calendar sync pages and commits in one go, so a worker
			// already running when the account was disabled or removed cannot keep
			// committing for it — a check only at job entry would miss every later page.
			var stillEnabled = await context
				.Accounts.Where(a => a.Id == account.Id)
				.Select(a => a.IsEnabled)
				.FirstOrDefaultAsync(ct);
			if (!stillEnabled)
			{
				return;
			}

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
			rebaseObservedProviderIds?.UnionWith(page.Upserted.Select(dto => dto.ProviderEventId));
			await ApplyPageAsync(
				account,
				calendarId,
				page,
				commitCursor: !page.HasMore,
				resolvingEventId,
				rebaseObservedProviderIds,
				rebaseWindowStart,
				rebaseWindowEnd,
				ct
			);
			continuation = page.Continuation;
			if (continuation is null)
			{
				return;
			}
		}
	}

	/// <summary>
	/// A rolling Graph window changes coverage, not provider identity. Retain canonical rows so
	/// open editors and unresolved conflicts remain valid while a fresh delta baseline upserts
	/// the new horizon under the same local ids.
	/// </summary>
	private async Task ResetGraphWindowAsync(Guid calendarId, CancellationToken ct)
	{
		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		var calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
		calendar.SyncCursor = null;
		calendar.SyncWindowStartedAt = null;
		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
	}

	/// <summary>
	/// A rejected sync token cannot safely be advanced. Discard the stale local baseline before
	/// requesting a fresh one, so the next successful final page owns both rows and its token.
	/// </summary>
	private async Task ResetBaselineAsync(Guid calendarId, CancellationToken ct)
	{
		var removedIds = await context.CalendarEvents.Where(e => e.CalendarId == calendarId && !e.SyncConflict).Select(e => e.Id).ToListAsync(ct);
		await using var transaction = await context.Database.BeginTransactionAsync(ct);
		context.CalendarEvents.RemoveRange(await context.CalendarEvents.Where(e => e.CalendarId == calendarId && !e.SyncConflict).ToListAsync(ct));
		var calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
		calendar.SyncCursor = null;
		calendar.SyncWindowStartedAt = null;
		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		foreach (var eventId in removedIds)
		{
			await events.CalendarEventUpdatedAsync(eventId);
		}
	}

	private async Task ApplyPageAsync(
		Account account,
		Guid calendarId,
		CalendarSyncResult page,
		bool commitCursor,
		Guid? resolvingEventId,
		IReadOnlySet<string>? rebaseObservedProviderIds,
		DateTimeOffset? rebaseWindowStart,
		DateTimeOffset? rebaseWindowEnd,
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
		await NormalizeLegacyCalDavResourceIdsAsync(account, calendarId, ct);
		await context.SaveChangesAsync(ct);
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
					.Calendars.Where(c => c.AccountId == account.Id && c.IsLocalOnly)
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

		// A normal sync can observe a just-created resource before a provider-specific exact
		// lookup sees it. In that case the same transaction materialises the event and removes
		// its durable ambiguous-create record; either both persist or neither does (§6).
		var observedUids = page.Upserted.Select(dto => dto.ICalUid).Distinct().ToList();
		var observedCreationKeys = page.Upserted
			.Select(dto => dto.ProviderCreationKey)
			.Where(key => key is not null)
			.Cast<string>()
			.Distinct()
			.ToList();
		if (observedUids.Count > 0 || observedCreationKeys.Count > 0)
		{
			context.CalendarCreationAttempts.RemoveRange(
				await context
					.CalendarCreationAttempts.Where(a =>
						a.CalendarId == calendarId
						&& (observedUids.Contains(a.ICalUid) || observedCreationKeys.Contains(a.ProviderCreationKey))
					)
					.ToListAsync(ct)
			);
		}

		// Flushed before the recurrence-resolution queries below: those run as ordinary
		// SingleAsync lookups against the database, which cannot see an Add()ed entity that
		// has not been saved yet — including one added earlier in this same page.
		await context.SaveChangesAsync(ct);

		// A recurrence relation refers to the local canonical event id, while providers carry
		// the master's volatile id. Resolve it inside this transaction after all page upserts,
		// so either page order is safe and no provider id leaks into persisted intent.
		//
		// Batched rather than one SingleAsync/SingleOrDefaultAsync pair per upserted event: a
		// page can hold hundreds of occurrences, and the per-item version issued two queries
		// for every single one of them — including every non-recurring event, whose
		// RecurrenceMasterProviderEventId is null and so was still spending a query looking
		// for a row that could never match.
		// Loaded in two passes rather than guessing every referenced master id up front: an
		// event skipped above because it's an unresolved conflict never had Apply() run, so
		// its stored RecurrenceMasterProviderEventId can differ from what this page's dto
		// reports, and the old per-item lookup always read that stored value fresh from the
		// DB. Only the first pass's actual rows can say what the real referenced master ids
		// are.
		var pageProviderIds = page.Upserted.Select(dto => dto.ProviderEventId).Distinct().ToList();
		var byProviderId = await context
			.CalendarEvents.Where(e =>
				e.CalendarId == calendarId && e.ProviderEventId != null && pageProviderIds.Contains(e.ProviderEventId)
			)
			.ToDictionaryAsync(e => e.ProviderEventId!, ct);

		var masterProviderIds = byProviderId
			.Values.Select(e => e.RecurrenceMasterProviderEventId)
			.Where(id => id is not null && !byProviderId.ContainsKey(id))
			.Distinct()
			.ToList();
		if (masterProviderIds.Count > 0)
		{
			var masters = await context
				.CalendarEvents.Where(e =>
					e.CalendarId == calendarId && e.ProviderEventId != null && masterProviderIds.Contains(e.ProviderEventId)
				)
				.ToListAsync(ct);
			foreach (var master in masters)
			{
				byProviderId[master.ProviderEventId!] = master;
			}
		}

		foreach (var dto in page.Upserted)
		{
			var occurrence = byProviderId[dto.ProviderEventId];
			var master =
				occurrence.RecurrenceMasterProviderEventId is string masterId ? byProviderId.GetValueOrDefault(masterId) : null;
			occurrence.RecurrenceMasterId = master?.Id;
		}

		var localMasterIds = page
			.Upserted.Where(dto => dto.RecurrenceMasterProviderEventId is null)
			.Select(dto => byProviderId[dto.ProviderEventId].Id)
			.ToList();
		if (localMasterIds.Count > 0)
		{
			var masterProviderIdsByLocalMasterId = page
				.Upserted.Where(dto => dto.RecurrenceMasterProviderEventId is null)
				.ToDictionary(dto => byProviderId[dto.ProviderEventId].Id, dto => dto.ProviderEventId);
			var waitingChildren = await context
				.CalendarEvents.Where(e =>
					e.CalendarId == calendarId
					&& e.RecurrenceMasterProviderEventId != null
					&& masterProviderIdsByLocalMasterId.Values.Contains(e.RecurrenceMasterProviderEventId)
				)
				.ToListAsync(ct);
			var childrenByMasterProviderId = waitingChildren.ToLookup(e => e.RecurrenceMasterProviderEventId);
			foreach (var localMasterId in localMasterIds)
			{
				var masterProviderId = masterProviderIdsByLocalMasterId[localMasterId];
				foreach (var child in childrenByMasterProviderId[masterProviderId])
				{
					child.RecurrenceMasterId = localMasterId;
					changed.Add(child.Id);
				}
			}
		}

		if (commitCursor
			&& rebaseObservedProviderIds is not null
			&& rebaseWindowStart is { } windowStart
			&& rebaseWindowEnd is { } windowEnd)
		{
			var stale = (await context.CalendarEvents.Where(e => e.CalendarId == calendarId && !e.SyncConflict).ToListAsync(ct))
				.Where(e => !rebaseObservedProviderIds.Contains(e.ProviderEventId))
				.ToList();
		}

		if (commitCursor)
		{
			var calendar = await context.Calendars.SingleAsync(c => c.Id == calendarId, ct);
			calendar.SyncCursor = page.NewCursor;
			if (account.ProviderType == ProviderType.Microsoft365 && calendar.SyncWindowStartedAt is null)
			{
				calendar.SyncWindowStartedAt = DateTimeOffset.UtcNow;
			}
		}

		await context.SaveChangesAsync(ct);
		await transaction.CommitAsync(ct);
		foreach (var eventId in changed)
		{
			await events.CalendarEventUpdatedAsync(eventId);
		}
		faults.Reached(FaultPoints.SyncPageAfterCommit);
	}

	/// <summary>
	/// CalDAV used to persist an absolute href while current providers emit its path/query
	/// identity. Normalize both an event and its recurrence-master reference before page
	/// matching, so a pre-upgrade row is updated in place rather than duplicated and then
	/// silently bypassed by a committed cursor.
	/// </summary>
	private async Task NormalizeLegacyCalDavResourceIdsAsync(Account account, Guid calendarId, CancellationToken ct)
	{
		if (account.ProviderType != ProviderType.Imap)
		{
			return;
		}

		var events = await context.CalendarEvents.Where(row => row.CalendarId == calendarId).ToListAsync(ct);
		var aliasGroups = events
			.Where(row => row.ProviderEventId is not null)
			.GroupBy(row => NormalizeResourceIdentity(row.ProviderEventId))
			.Where(group => group.Key is not null && group.Count() > 1)
			.ToList();
		foreach (var aliases in aliasGroups)
		{
			var canonicalId = aliases.Key!;
			var survivor = aliases.FirstOrDefault(row => row.SyncConflict) ?? aliases.First();
			foreach (var duplicate in aliases.Where(row => row.Id != survivor.Id))
			{
				foreach (var child in events.Where(row => row.RecurrenceMasterId == duplicate.Id))
				{
					child.RecurrenceMasterId = survivor.Id;
				}
				survivor.SyncConflict |= duplicate.SyncConflict;
				context.CalendarEvents.Remove(duplicate);
				events.Remove(duplicate);
			}

			// Delete aliases before assigning the surviving row its canonical value: SQLite
			// enforces this unique key statement-by-statement, not at transaction commit.
			await context.SaveChangesAsync(ct);
			survivor.ProviderEventId = canonicalId;
		}

		foreach (var row in events)
		{
			row.ProviderEventId = NormalizeResourceIdentity(row.ProviderEventId)
				?? throw new InvalidOperationException("Calendar event provider identity cannot be null.");
			row.RecurrenceMasterProviderEventId = NormalizeResourceIdentity(row.RecurrenceMasterProviderEventId);
		}
	}

	private static string? NormalizeResourceIdentity(string? providerEventId)
	{
		if (providerEventId is null)
		{
			return null;
		}

		var suffixIndex = providerEventId.IndexOf('#');
		var resource = suffixIndex < 0 ? providerEventId : providerEventId[..suffixIndex];
		if (Uri.TryCreate(resource, UriKind.Absolute, out var uri)
			&& (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
		{
			return uri.PathAndQuery + (suffixIndex < 0 ? string.Empty : providerEventId[suffixIndex..]);
		}
		return providerEventId;
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
