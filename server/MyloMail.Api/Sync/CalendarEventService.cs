using Hangfire;
using Ical.Net.DataTypes;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>What the calendar UI sends when it creates or edits an event.</summary>
public sealed record CalendarEventInput(
	Guid? EventId,
	Guid CalendarId,
	string Title,
	string? Location,
	string? Description,
	DateTimeOffset Start,
	DateTimeOffset End,
	bool IsAllDay,
	string? StartTimeZoneId = null,
	string? EndTimeZoneId = null,
	IReadOnlyList<string>? RecurrenceRules = null,
	IReadOnlyList<DateTimeOffset>? RecurrenceDates = null,
	IReadOnlyList<DateTimeOffset>? ExceptionDates = null
);

/// <summary>
/// Calendar event CRUD against the provider, synchronously, like <see cref="MailboxManagement"/>
/// (§2, §6).
/// </summary>
/// <remarks>
/// Deliberately not the message mutation queue: that chain is keyed by message and ordered
/// per-message for a reason that has nothing to do with calendar events, and forcing this
/// through it would make the queue a general-purpose lock manager rather than mail's own
/// coordination domain. A calendar event is a single document with no occurrence/mailbox
/// membership to reconcile, so a direct provider call — succeed or fail, told to the caller —
/// is honest about what happened; the queue's asynchronous "enqueue now, learn later" contract
/// exists for problems this domain does not have.
/// </remarks>
public sealed class CalendarEventService(
	MyloMailDbContext context,
	ICalendarProviderFactory providers,
	IHubEvents events,
	CalendarSyncService calendarSync,
	ItipReplySender itipReply,
	ILogger<CalendarEventService> logger,
	IFaultInjector faults,
	IBackgroundJobClient? jobs = null
)
{
	public async Task<CalendarEvent> SaveAsync(CalendarEventInput input, CancellationToken ct = default)
	{
		input = Normalize(input);
		var calendar = await context.Calendars.FirstAsync(c => c.Id == input.CalendarId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == calendar.AccountId, ct);
		var provider = providers.For(account);
		using var disposable = provider as IDisposable;

		var existing = input.EventId is { } id
			? await context.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct)
			: null;

		// A dangling EventId (already deleted elsewhere) falls through to CreateAsync just like
		// no EventId at all — only a definite mismatch against another calendar is rejected.
		// Left unchecked, UpdateAsync would mutate `existing` and push the edit through
		// `account`'s provider credentials — a different account than the one `existing`
		// actually belongs to, corrupting a foreign event with this account's authentication.
		if (existing is not null && existing.CalendarId != input.CalendarId)
		{
			throw new HubException(
				$"Calendar event {existing.Id} does not belong to calendar {input.CalendarId}."
			);
		}

		// EventModal.tsx's Start/End fields are plain text inputs with no min/max tying one to
		// the other — nothing stops a user (or a direct SaveCalendarEvent call) from picking an
		// End before Start. RFC 5545 has no negative-duration VEVENT shape, so this would reach
		// CalDavIcs.RenderVEvent as a malformed resource rather than a clean rejection here.
		if (input.End < input.Start)
		{
			throw new HubException("An event cannot end before it starts.");
		}
		try
		{
			provider.ValidateEvent(ToDto(input));
		}
		catch (NotSupportedException ex)
		{
			throw new HubException(ex.Message);
		}

		return existing is null
			? await CreateAsync(account, calendar, input, provider, ct)
			: await UpdateAsync(account, existing, input, provider, ct);
	}

	private async Task<CalendarEvent> CreateAsync(
		Account account,
		Calendar calendar,
		CalendarEventInput input,
		ICalendarProvider provider,
		CancellationToken ct
	)
	{
		var creationId = Guid.NewGuid();
		var attempt = new CalendarCreationAttempt
		{
			Id = Guid.NewGuid(),
			ProviderCreationKey = $"m{creationId:N}",
			CalendarId = calendar.Id,
			ICalUid = $"{creationId:N}@mylomail.local",
			Title = input.Title,
			Location = input.Location,
			Description = input.Description,
			Start = input.Start,
			End = input.End,
			IsAllDay = input.IsAllDay,
			StartTimeZoneId = input.StartTimeZoneId,
			EndTimeZoneId = input.EndTimeZoneId,
			RecurrenceRules = input.RecurrenceRules ?? [],
			RecurrenceDates = input.RecurrenceDates ?? [],
			ExceptionDates = input.ExceptionDates ?? [],
			// Committed before the provider call. It records ambiguity, never success (§6).
			DispatchedAt = DateTimeOffset.UtcNow,
		};
		context.CalendarCreationAttempts.Add(attempt);
		await context.SaveChangesAsync(ct);
		CalendarEventCreation createdResult;
		try
		{
			createdResult = await RunProviderCallAsync(() => provider.CreateEventAsync(account, calendar, ToDto(attempt), ct));
		}
		catch
		{
			jobs?.Enqueue<Scheduling.SyncJobs>(job => job.CalendarCreationRecoveryAsync(account.Id, default));
			throw;
		}
		if (string.IsNullOrEmpty(createdResult.ProviderRevision))
		{
			jobs?.Enqueue<Scheduling.SyncJobs>(job => job.CalendarCreationRecoveryAsync(account.Id, default));
			throw new InvalidOperationException("Calendar creation requires a provider revision.");
		}
		try
		{
			faults.Reached(FaultPoints.CalendarCreateAfterProviderCallBeforeCommit);

			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			var claimed = await context.CalendarCreationAttempts.Where(a => a.Id == attempt.Id).ExecuteDeleteAsync(ct);
			if (claimed == 0)
			{
				await transaction.RollbackAsync(ct);
				return await context.CalendarEvents.SingleAsync(
					e => e.CalendarId == attempt.CalendarId && e.ProviderEventId == createdResult.ProviderEventId,
					ct
				);
			}

			var created = Materialise(attempt, createdResult);
			context.CalendarEvents.Add(created);
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
			await events.CalendarEventUpdatedAsync(created.Id);
			return created;
		}
		catch
		{
			jobs?.Enqueue<Scheduling.SyncJobs>(job => job.CalendarCreationRecoveryAsync(account.Id, default));
			throw;
		}
	}

	private async Task<CalendarEvent> UpdateAsync(
		Account account,
		CalendarEvent existing,
		CalendarEventInput input,
		ICalendarProvider provider,
		CancellationToken ct
	)
	{
		existing.Title = input.Title;
		existing.Location = input.Location;
		existing.Description = input.Description;
		existing.Start = input.Start;
		existing.End = input.End;
		existing.IsAllDay = input.IsAllDay;
		existing.StartTimeZoneId = input.StartTimeZoneId;
		existing.EndTimeZoneId = input.EndTimeZoneId;
		existing.RecurrenceRules = input.RecurrenceRules ?? [];
		existing.RecurrenceDates = input.RecurrenceDates ?? [];
		existing.ExceptionDates = input.ExceptionDates ?? [];
		existing.Sequence++;

		// A conflict does not lose the edit: it stays applied locally, flagged, so the user's
		// work survives and can be reconciled once they've seen the server's copy — the
		// detect-don't-merge contract in §15, not a silent overwrite either direction.
		try
		{
			await provider.UpdateEventAsync(account, existing, existing.ProviderRevision, ct);
			await PropagateSharedProviderRevisionAsync(provider, existing, ct);
			existing.SyncConflict = false;
		}
		catch (ProviderConflictException)
		{
			existing.SyncConflict = true;
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not HubException)
		{
			logger.LogWarning(ex, "A calendar provider call was rejected.");
			throw new HubException(ex.Message);
		}
		if (!existing.SyncConflict)
		{
			faults.Reached(FaultPoints.CalendarUpdateAfterProviderCallBeforeCommit);
		}

		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(existing.Id);
		if (existing.SyncConflict)
		{
			await events.CalendarConflictDetectedAsync(existing.Id);
		}
		return existing;
	}
	private async Task PropagateSharedProviderRevisionAsync(
		ICalendarProvider provider,
		CalendarEvent updated,
		CancellationToken ct
	)
	{
		if (!provider.SharesRevisionAcrossRecurrenceSet || string.IsNullOrEmpty(updated.ProviderRevision))
		{
			return;
		}

		var siblings = await context.CalendarEvents
			.Where(e =>
				e.Id != updated.Id
				&& e.CalendarId == updated.CalendarId
				&& e.ICalUid == updated.ICalUid
			)
			.ToListAsync(ct);
		foreach (var sibling in siblings)
		{
			sibling.ProviderRevision = updated.ProviderRevision;
		}
	}

	/// <summary>
	/// "Keep mine / keep theirs" for a flagged conflict (§15) — the only two ways out of
	/// <see cref="CalendarEvent.SyncConflict"/> besides a routine sync pass happening to pull
	/// the same server state anyway.
	/// </summary>
	/// <param name="keepMine">
	/// True forces an unconditional overwrite of the server's copy with what is held locally
	/// (no <c>If-Match</c> precondition, since the user has explicitly chosen to overwrite
	/// whatever is there now, not what was last read). False discards the local edit and pulls
	/// the server's current version via an ordinary account sync — there is no
	/// single-event-fetch method on <see cref="ICalendarProvider"/> (§2), and reusing the
	/// already-correct sync path is safer than inventing a second, narrower one.
	/// </param>
	public async Task<CalendarEvent> ResolveConflictAsync(
		Guid eventId,
		bool keepMine,
		CancellationToken ct = default
	)
	{
		var existing = await context.CalendarEvents.FirstAsync(e => e.Id == eventId, ct);
		if (!existing.SyncConflict)
		{
			return existing;
		}

		var calendar = await context.Calendars.FirstAsync(c => c.Id == existing.CalendarId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == calendar.AccountId, ct);

		if (keepMine)
		{
			var provider = providers.For(account);
			using var disposable = provider as IDisposable;
			await RunProviderCallAsync(() => provider.UpdateEventAsync(account, existing, expectedETag: null, ct));
			await PropagateSharedProviderRevisionAsync(provider, existing, ct);
			existing.SyncConflict = false;
			faults.Reached(FaultPoints.CalendarUpdateAfterProviderCallBeforeCommit);
			await context.SaveChangesAsync(ct);
			await events.CalendarEventUpdatedAsync(existing.Id);
			return existing;
		}

		// Looked up again afterward by (CalendarId, ProviderEventId), not by the local `Id`
		// captured above: a rejected sync cursor forces `CalendarSyncService` to discard and
		// re-create every local row for that calendar under fresh ids (§3), which would
		// otherwise make the plain `FirstAsync(e => e.Id == eventId, ...)` this used to end
		// with throw, having nothing left to find.
		var calendarId = existing.CalendarId;
		var providerEventId = existing.ProviderEventId;
		await calendarSync.SynchronizeAsync(account, existing.Id, ct);
		return await context.CalendarEvents.FirstAsync(
			e => e.CalendarId == calendarId && e.ProviderEventId == providerEventId,
			ct
		);
	}

	/// <summary>
	/// Deletes an event and, if it was a recurrence master, the override instances that only
	/// exist relative to it — the same set the sync path removes for a provider-side deletion
	/// (§1). A provider rejection (conflict, or already gone) leaves everything local as it was;
	/// there is nothing to reconcile against a delete that did not happen.
	/// </summary>
	/// <summary>
	/// Accept/Decline/Tentative on an invite (§13 Epic 7). Graph and Google Calendar handle
	/// this via their own native APIs; on the CalDAV/IMAP path the provider generates an iTIP
	/// <c>REPLY</c> and sends it as mail, which is why this resolves the replying identity's
	/// own address here rather than leaving the provider layer to query the database for it.
	/// </summary>
	public async Task RespondToInviteAsync(
		Guid eventId,
		InviteResponse response,
		string? comment,
		CancellationToken ct = default
	)
	{
		var ev = await context.CalendarEvents.FirstAsync(e => e.Id == eventId, ct);
		var calendar = await context.Calendars.FirstAsync(c => c.Id == ev.CalendarId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == calendar.AccountId, ct);
		var identity = await context
			.SendIdentities.Where(i => i.AccountId == account.Id && i.IsDefault)
			.FirstAsync(ct);

		var replyingAs = new Address(identity.DisplayName, identity.EmailAddress);
		if (calendar.IsLocalOnly)
		{
			// No provider configured at all for this account — there is nothing to ask via
			// ICalendarProviderFactory, which would throw ProviderNotConfiguredException. The
			// reply is mail either way, so send it directly (§13 Epic 7).
			await RunProviderCallAsync(() => itipReply.SendAsync(account, ev, response, comment, replyingAs, ct));
		}
		else
		{
			var provider = providers.For(account);
			using var disposable = provider as IDisposable;
			await RunProviderCallAsync(() =>
				provider.RespondToInviteAsync(account, ev, response, comment, replyingAs, ct)
			);
		}

		// The REPLY only reaches the organiser's inbox — nothing about sending it changes this
		// event's own stored attendee list, and a synced-back PARTSTAT update from the
		// organiser's server is not guaranteed to arrive promptly, if at all, for every
		// provider. Recording the just-taken response locally is what lets the UI show it
		// immediately rather than only after some future sync happens to reflect it back.
		var status = response switch
		{
			InviteResponse.Accept => ResponseStatus.Accepted,
			InviteResponse.Decline => ResponseStatus.Declined,
			_ => ResponseStatus.Tentative,
		};
		var updated = ev.Attendees.Select(a =>
			string.Equals(a.Email, identity.EmailAddress, StringComparison.OrdinalIgnoreCase)
				? a with { ResponseStatus = status }
				: a
		);
		ev.Attendees = [.. updated];
		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(eventId);
	}

	public async Task DeleteAsync(Guid eventId, CancellationToken ct = default)
	{
		var ev = await context.CalendarEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
		if (ev is null)
		{
			return;
		}

		var calendar = await context.Calendars.FirstAsync(c => c.Id == ev.CalendarId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == calendar.AccountId, ct);
		var provider = providers.For(account);
		using var disposable = provider as IDisposable;

		await RunProviderCallAsync(() => provider.DeleteEventAsync(account, ev, ct));

		// A recurrence-override instance is not removed: the CalDAV provider's own
		// DeleteEventAsync (and every provider's, per §1) marks it STATUS:CANCELLED in place
		// rather than deleting anything, since the override's "resource" is the whole series —
		// the row this app keeps locally must reflect the same outcome. Deleting the local row
		// instead would leave `CalendarEventOccurrences.ForCalendarAsync`'s override map without
		// this occurrence's RecurrenceId until the next sync happens to pull the cancelled
		// instance back down, and in that window `CalendarRecurrenceExpander` regenerates a
		// "ghost" virtual occurrence at the exact slot the user just deleted.
		if (ev.RecurrenceMasterId is not null)
		{
			ev.Status = EventStatus.Cancelled;
			await context.SaveChangesAsync(ct);
			await events.CalendarEventUpdatedAsync(eventId);
			return;
		}

		var children = await context.CalendarEvents.Where(e => e.RecurrenceMasterId == ev.Id).ToListAsync(ct);
		context.CalendarEvents.RemoveRange(children);
		context.CalendarEvents.Remove(ev);
		await context.SaveChangesAsync(ct);

		await events.CalendarEventUpdatedAsync(eventId);
		foreach (var child in children)
		{
			await events.CalendarEventUpdatedAsync(child.Id);
		}
	}

	private static CalendarEventDto ToDto(CalendarEventInput input) => new()
	{
		ProviderEventId = string.Empty,
		ICalUid = string.Empty,
		Title = input.Title,
		Location = input.Location,
		Description = input.Description,
		Start = input.Start,
		End = input.End,
		IsAllDay = input.IsAllDay,
		StartTimeZoneId = input.StartTimeZoneId,
		EndTimeZoneId = input.EndTimeZoneId,
		RecurrenceRules = input.RecurrenceRules ?? [],
		RecurrenceDates = input.RecurrenceDates ?? [],
		ExceptionDates = input.ExceptionDates ?? [],
	};

	internal static CalendarEventDto ToDto(CalendarCreationAttempt attempt) => new()
	{
		ProviderEventId = string.Empty,
		ICalUid = attempt.ICalUid,
		Title = attempt.Title,
		Location = attempt.Location,
		Description = attempt.Description,
		Start = attempt.Start,
		End = attempt.End,
		ProviderCreationKey = attempt.ProviderCreationKey,
		StartTimeZoneId = attempt.StartTimeZoneId,
		EndTimeZoneId = attempt.EndTimeZoneId,
		RecurrenceRules = attempt.RecurrenceRules,
		RecurrenceDates = attempt.RecurrenceDates,
		ExceptionDates = attempt.ExceptionDates,
		IsAllDay = attempt.IsAllDay,
	};

	internal static CalendarEvent Materialise(CalendarCreationAttempt attempt, CalendarEventCreation created) =>
		new()
		{
			Id = attempt.Id,
			CalendarId = attempt.CalendarId,
			ProviderEventId = created.ProviderEventId,
			ProviderRevision = created.ProviderRevision,
			ICalUid = created.ICalUid ?? attempt.ICalUid,
			Title = attempt.Title,
			Location = attempt.Location,
			Description = attempt.Description,
			Start = attempt.Start,
			End = attempt.End,
			IsAllDay = attempt.IsAllDay,
			StartTimeZoneId = attempt.StartTimeZoneId,
			EndTimeZoneId = attempt.EndTimeZoneId,
			RecurrenceRules = attempt.RecurrenceRules,
			RecurrenceDates = attempt.RecurrenceDates,
			ExceptionDates = attempt.ExceptionDates,
		};

	private static CalendarEventInput Normalize(CalendarEventInput input)
	{
		var rules = (input.RecurrenceRules ?? [])
			.Select(rule => rule.Trim())
			.Where(rule => rule.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		if (rules.Length > 32 || rules.Any(rule => rule.Length > 2048))
		{
			throw new HubException("An event cannot contain more than 32 bounded recurrence rules.");
		}
		foreach (var rule in rules)
		{
			try
			{
				_ = new RecurrencePattern(rule);
			}
			catch (Exception)
			{
				throw new HubException($"'{rule}' is not a valid RFC 5545 recurrence rule.");
			}
		}

		var recurrenceDates = NormalizeDates(input.RecurrenceDates, input.IsAllDay, "additional");
		var exceptionDates = NormalizeDates(input.ExceptionDates, input.IsAllDay, "excluded");
		var start = input.IsAllDay ? AllDayDate(input.Start) : input.Start;
		var end = input.IsAllDay ? AllDayDate(input.End) : input.End;

		return input with
		{
			Start = start,
			End = end,
			StartTimeZoneId = input.IsAllDay ? null : CanonicalTimeZoneId(input.StartTimeZoneId),
			EndTimeZoneId = input.IsAllDay ? null : CanonicalTimeZoneId(input.EndTimeZoneId),
			RecurrenceRules = rules,
			RecurrenceDates = recurrenceDates,
			ExceptionDates = exceptionDates,
		};
	}

	private static IReadOnlyList<DateTimeOffset> NormalizeDates(
		IReadOnlyList<DateTimeOffset>? values,
		bool isAllDay,
		string description
	)
	{
		if (values is { Count: > 512 })
		{
			throw new HubException($"An event cannot contain more than 512 {description} recurrence dates.");
		}
		return
		[
			.. (values ?? [])
				.Select(value => isAllDay ? AllDayDate(value) : value)
				.Distinct()
				.Order(),
		];
	}

	private static DateTimeOffset AllDayDate(DateTimeOffset value) =>
		new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);

	private static string? CanonicalTimeZoneId(string? value)
	{
		var candidate = CalendarTimeZoneIds.Canonicalize(value);
		if (candidate is null)
		{
			return null;
		}
		try
		{
			_ = TimeZoneInfo.FindSystemTimeZoneById(candidate);
			return candidate;
		}
		catch (TimeZoneNotFoundException)
		{
			throw new HubException($"'{value}' is not a recognised IANA time zone.");
		}
		catch (InvalidTimeZoneException)
		{
			throw new HubException($"'{value}' is not a usable IANA time zone.");
		}
	}

	/// <summary>
	/// Surfaces a provider's rejection of a calendar call to the caller with its real message,
	/// instead of SignalR's default "An unexpected error occurred" (detailed errors are off,
	/// matching every other hub method). EventModal.tsx and ReadingPane.tsx's InviteBanner both
	/// exist specifically to show the user why a save, delete, or RSVP reply was rejected, which
	/// is silently defeated unless the failure is rethrown as a <see cref="HubException"/>, the
	/// one exception type SignalR forwards verbatim. Mirrors <c>MailboxManagement</c>'s helper of
	/// the same name and shape.
	/// </summary>
	private async Task RunProviderCallAsync(Func<Task> call)
	{
		try
		{
			await call();
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not HubException)
		{
			logger.LogWarning(ex, "A calendar provider call was rejected.");
			throw new HubException(ex.Message);
		}
	}

	private async Task<T> RunProviderCallAsync<T>(Func<Task<T>> call)
	{
		try
		{
			return await call();
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not HubException)
		{
			logger.LogWarning(ex, "A calendar provider call was rejected.");
			throw new HubException(ex.Message);
		}
	}
}
