using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
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
	bool IsAllDay
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
	IHubEvents events
)
{
	public async Task<CalendarEvent> SaveAsync(CalendarEventInput input, CancellationToken ct = default)
	{
		var calendar = await context.Calendars.FirstAsync(c => c.Id == input.CalendarId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == calendar.AccountId, ct);
		var provider = providers.For(account);

		var existing = input.EventId is { } id
			? await context.CalendarEvents.FirstOrDefaultAsync(e => e.Id == id, ct)
			: null;

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
		var dto = new CalendarEventDto
		{
			ProviderEventId = string.Empty,
			ICalUid = $"{Guid.NewGuid():N}@mylomail.local",
			Title = input.Title,
			Location = input.Location,
			Description = input.Description,
			Start = input.Start,
			End = input.End,
			IsAllDay = input.IsAllDay,
		};

		// The provider is asked first: it assigns the resource identity (an href, for CalDAV),
		// and a local row with no provider identity would be a canonical event this account
		// cannot ever sync, update or delete again (§1).
		var providerEventId = await provider.CreateEventAsync(account, calendar, dto, ct);

		var created = new CalendarEvent
		{
			Id = Guid.NewGuid(),
			CalendarId = calendar.Id,
			ProviderEventId = providerEventId,
			ICalUid = dto.ICalUid,
			Title = input.Title,
			Location = input.Location,
			Description = input.Description,
			Start = input.Start,
			End = input.End,
			IsAllDay = input.IsAllDay,
		};
		context.CalendarEvents.Add(created);
		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(created.Id);
		return created;
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
		existing.Sequence++;

		// A conflict does not lose the edit: it stays applied locally, flagged, so the user's
		// work survives and can be reconciled once they've seen the server's copy — the
		// detect-don't-merge contract in §15, not a silent overwrite either direction.
		try
		{
			await provider.UpdateEventAsync(account, existing, existing.ProviderRevision, ct);
			existing.SyncConflict = false;
		}
		catch (ProviderConflictException)
		{
			existing.SyncConflict = true;
		}

		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(existing.Id);
		if (existing.SyncConflict)
		{
			await events.CalendarConflictDetectedAsync(existing.Id);
		}
		return existing;
	}

	/// <summary>
	/// Deletes an event and, if it was a recurrence master, the override instances that only
	/// exist relative to it — the same set the sync path removes for a provider-side deletion
	/// (§1). A provider rejection (conflict, or already gone) leaves everything local as it was;
	/// there is nothing to reconcile against a delete that did not happen.
	/// </summary>
	public async Task DeleteAsync(Guid eventId, CancellationToken ct = default)
	{
		var ev = await context.CalendarEvents.FirstOrDefaultAsync(e => e.Id == eventId, ct);
		if (ev is null)
		{
			return;
		}

		var calendar = await context.Calendars.FirstAsync(c => c.Id == ev.CalendarId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == calendar.AccountId, ct);

		await providers.For(account).DeleteEventAsync(account, ev, ct);

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
}
