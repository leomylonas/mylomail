using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// Turns a received meeting invite into a <see cref="CalendarEvent"/> a user can respond to
/// (§1, §13 Epic 7) — the "from message" half of Epic 7's RSVP requirement; the "from calendar"
/// half already existed via <see cref="CalendarEventService.RespondToInviteAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Runs once per content fetch, not on message open (see the call site in
/// <c>ContentAcquisition.AcquireAsync</c>): materialising on ingest gives this an idempotency
/// boundary for free — <see cref="CalendarEvent.ICalUid"/> is checked before creating anything,
/// so re-ingesting the same bytes (a <c>RawVersion</c> bump) never duplicates the event, the
/// same guarantee a user-triggered, repeatable "open this message" hook could not offer as
/// cheaply.
/// </para>
/// <para>
/// Only <c>METHOD:REQUEST</c> is handled. A cancellation or someone else's reply arriving as
/// mail (<c>METHOD:CANCEL</c>/<c>METHOD:REPLY</c>) is deliberately left alone for now — acting
/// on those correctly needs the same care §15 gives conflict resolution elsewhere, and getting
/// it wrong (silently cancelling or attendee-updating the wrong event) is worse than not acting.
/// Only the recurrence master/single instance (no <c>RECURRENCE-ID</c>) is materialised; an
/// override instance in the same .ics is skipped rather than guessed at.
/// </para>
/// </remarks>
public sealed class MailInviteMaterializer(MyloMailDbContext context, IHubEvents events)
{
	public async Task MaterializeFromMessageAsync(
		Account account,
		Guid messageId,
		MimeMessage mime,
		CancellationToken ct = default
	)
	{
		var part = mime.BodyParts
			.OfType<MimePart>()
			.FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"));
		if (part?.Content is null)
		{
			return;
		}

		using var stream = new MemoryStream();
		await part.Content.DecodeToAsync(stream, ct);
		var ics = System.Text.Encoding.UTF8.GetString(stream.ToArray());

		if (!string.Equals(CalDavIcs.ParseMethod(ics), "REQUEST", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		// Synthetic href: this .ics has no CalDAV resource, only the message it arrived in.
		// ParseEvents only uses it to derive ProviderEventId, which a locally-materialised
		// event has no other use for.
		var parsed = CalDavIcs.ParseEvents(ics, $"mail:{messageId}", string.Empty);

		foreach (var dto in parsed.Where(e => e.RecurrenceId is null))
		{
			await UpsertAsync(account, dto, ct);
		}
	}

	private async Task UpsertAsync(Account account, CalendarEventDto dto, CancellationToken ct)
	{
		var calendarIds = await context
			.Calendars.Where(c => c.AccountId == account.Id)
			.Select(c => c.Id)
			.ToListAsync(ct);

		var existing = await context.CalendarEvents.FirstOrDefaultAsync(
			e => e.ICalUid == dto.ICalUid && calendarIds.Contains(e.CalendarId),
			ct
		);

		// An out-of-order or duplicate delivery of an older version of the same invite must
		// not regress what a newer one (or the user's own local edit, whose Sequence this
		// account never advances) already established.
		if (existing is not null && dto.Sequence < existing.Sequence)
		{
			return;
		}

		var target = existing ?? new CalendarEvent { Id = Guid.NewGuid() };
		if (existing is null)
		{
			target.CalendarId = await LocalCalendarIdAsync(account.Id, ct);
		}

		target.ProviderEventId = dto.ProviderEventId;
		target.ICalUid = dto.ICalUid;
		target.ProviderRevision = dto.ProviderRevision;
		target.Sequence = dto.Sequence;
		target.Title = dto.Title;
		target.Location = dto.Location;
		target.Description = dto.Description;
		target.Start = dto.Start;
		target.End = dto.End;
		target.StartTimeZoneId = dto.StartTimeZoneId;
		target.EndTimeZoneId = dto.EndTimeZoneId;
		target.IsAllDay = dto.IsAllDay;
		target.Organizer = dto.Organizer;
		target.Attendees = dto.Attendees;
		target.Status = dto.Status;
		target.Reminders = dto.Reminders;
		target.RecurrenceRules = dto.RecurrenceRules;
		target.RecurrenceDates = dto.RecurrenceDates;
		target.ExceptionDates = dto.ExceptionDates;

		if (existing is null)
		{
			context.CalendarEvents.Add(target);
		}
		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(target.Id);
	}

	/// <summary>Get-or-create the account's local-only pseudo-calendar (§1).</summary>
	private async Task<Guid> LocalCalendarIdAsync(Guid accountId, CancellationToken ct)
	{
		var existing = await context.Calendars.FirstOrDefaultAsync(
			c => c.AccountId == accountId && c.IsLocalOnly,
			ct
		);
		if (existing is not null)
		{
			return existing.Id;
		}

		var created = new Calendar
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			ProviderCalendarId = $"local-invites-{accountId:N}",
			Name = "Invites",
			// Never the account's default: a plain "new event" flow elsewhere assumes a
			// default calendar has a real provider behind it (§2) — this one deliberately
			// does not, and only ever gains events through invite materialisation.
			IsDefault = false,
			IsLocalOnly = true,
		};
		context.Calendars.Add(created);
		await context.SaveChangesAsync(ct);
		return created.Id;
	}
}
