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
/// half already existed via <see cref="CalendarEventService.RespondToInviteAsync"/>. Also
/// applies an attendee's <c>METHOD:REPLY</c> to the organiser's own materialised event, so the
/// organiser actually sees responses update — Epic 7's other requirement, previously unmet for
/// IMAP-based invites specifically (CalDAV/Graph accounts already get this via native sync).
/// </summary>
/// <remarks>
/// <para>
/// Runs once per content fetch, not on message open (see the call site in
/// <c>ContentAcquisition.AcquireAsync</c>): materialising on ingest gives this an idempotency
/// boundary for free — <see cref="CalendarEvent.ICalUid"/> is checked before creating anything,
/// so re-ingesting the same bytes (a <c>RawVersion</c> bump) never duplicates the event, the
/// same guarantee a user-triggered, repeatable "open this message" hook could not offer as
/// cheaply. The same idempotency covers <c>REPLY</c> processing below: re-ingesting the same
/// reply just reapplies the same <c>PARTSTAT</c>, which is a no-op.
/// </para>
/// <para>
/// A cancellation arriving as mail (<c>METHOD:CANCEL</c>) is still deliberately left alone —
/// silently cancelling the wrong event is a materially worse failure mode than not acting, and
/// unlike a reply's narrowly-scoped attendee-status update, a cancellation has no comparably
/// safe, conservative partial application. Only the recurrence master/single instance (no
/// <c>RECURRENCE-ID</c>) is materialised from a <c>REQUEST</c>; an override instance in the
/// same .ics is skipped rather than guessed at.
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
		var method = CalDavIcs.ParseMethod(ics);

		// Synthetic href: this .ics has no CalDAV resource, only the message it arrived in.
		// ParseEvents only uses it to derive ProviderEventId, which a locally-materialised
		// event has no other use for.
		var parsed = CalDavIcs.ParseEvents(ics, $"mail:{messageId}", string.Empty);

		if (string.Equals(method, "REQUEST", StringComparison.OrdinalIgnoreCase))
		{
			foreach (var dto in parsed.Where(e => e.RecurrenceId is null))
			{
				await UpsertAsync(account, dto, ct);
			}
		}
		else if (string.Equals(method, "REPLY", StringComparison.OrdinalIgnoreCase))
		{
			foreach (var dto in parsed)
			{
				await ApplyReplyAsync(account, dto, ct);
			}
		}
	}

	/// <summary>
	/// Applies one attendee's response to the organiser's own materialised event. Deliberately
	/// conservative on every axis a wrong guess here could not undo:
	/// </summary>
	/// <remarks>
	/// <list type="bullet">
	/// <item>No local event matches <see cref="CalendarEventDto.ICalUid"/> at all: this account
	/// was never the organiser (or never received/materialised the original invite) — ignored,
	/// not an error.</item>
	/// <item>The reply carries a <c>RECURRENCE-ID</c> but no override row exists locally for
	/// it: the deferred-override design above means only the master was ever materialised, so
	/// the response is applied to the master's attendee list — the closest local state
	/// actually exists to represent "this attendee responded to the series."</item>
	/// <item>The replying address is not one of the event's existing attendees: ignored rather
	/// than silently adding a new attendee no one invited.</item>
	/// <item>Not gated on <see cref="CalendarEventDto.Sequence"/>, unlike <see cref="UpsertAsync"/>'s
	/// content-regression guard: a reply legitimately references the <c>SEQUENCE</c> of the
	/// invite it answers, not the organiser's latest edit, so rejecting an older-sequence reply
	/// would silently drop a real response.</item>
	/// </list>
	/// <para>
	/// Known limitation, not yet guarded: two replies from the same attendee processed out of
	/// arrival order (e.g. a decline followed by an accept, but content-fetch retry/backfill
	/// delivers them in reverse) has no tiebreaker — <c>CalendarEventDto</c> carries no reply
	/// timestamp (<c>DTSTAMP</c>) to compare against, and adding one is a real schema change
	/// (a new persisted field on <see cref="Attendee"/>, plumbed through storage and generated
	/// types), not a small addition. The stale-overwrite window this leaves is recoverable, not
	/// silently permanent — a subsequent reply of either status corrects it — but a determined
	/// attacker or a genuinely unlucky redelivery order could show a wrong RSVP status until
	/// then.
	/// </para>
	/// </remarks>
	private async Task ApplyReplyAsync(Account account, CalendarEventDto dto, CancellationToken ct)
	{
		var respondingAttendee = dto.Attendees.FirstOrDefault();
		if (respondingAttendee is null)
		{
			return;
		}

		var calendarIds = await AccountCalendarIdsAsync(account.Id, ct);

		var candidates = await context
			.CalendarEvents.Where(e => e.ICalUid == dto.ICalUid && calendarIds.Contains(e.CalendarId))
			.ToListAsync(ct);
		if (candidates.Count == 0)
		{
			return;
		}

		var target =
			candidates.FirstOrDefault(e => e.RecurrenceId == dto.RecurrenceId)
			?? candidates.FirstOrDefault(e => e.RecurrenceId is null);
		if (target is null)
		{
			return;
		}

		var matchIndex = -1;
		for (var i = 0; i < target.Attendees.Count; i++)
		{
			if (string.Equals(target.Attendees[i].Email, respondingAttendee.Email, StringComparison.OrdinalIgnoreCase))
			{
				matchIndex = i;
				break;
			}
		}
		if (matchIndex < 0)
		{
			return;
		}

		var current = target.Attendees[matchIndex];
		if (current.ResponseStatus == respondingAttendee.ResponseStatus)
		{
			return;
		}

		var updated = target.Attendees.ToList();
		updated[matchIndex] = current with { ResponseStatus = respondingAttendee.ResponseStatus };
		target.Attendees = updated;

		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(target.Id);
	}

	private async Task UpsertAsync(Account account, CalendarEventDto dto, CancellationToken ct)
	{
		var calendarIds = await AccountCalendarIdsAsync(account.Id, ct);

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

	private Task<List<Guid>> AccountCalendarIdsAsync(Guid accountId, CancellationToken ct) =>
		context.Calendars.Where(c => c.AccountId == accountId).Select(c => c.Id).ToListAsync(ct);

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
