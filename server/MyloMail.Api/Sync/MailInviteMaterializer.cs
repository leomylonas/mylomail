using Microsoft.EntityFrameworkCore;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.CalDav;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Security;

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
public sealed class MailInviteMaterializer(
	MyloMailDbContext context,
	IHubEvents events,
	IIncomingMailAuthentication authentication
)
{
	private const int MaximumEventsPerCalendarPart = 64;

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

		var ics = await CalendarMimeReader.TryReadAsync(part, ct);
		if (ics is null)
		{
			return;
		}
		var method = CalDavIcs.ParseMethod(ics);

		// Synthetic href: this .ics has no CalDAV resource, only the message it arrived in.
		// ParseEvents only uses it to derive ProviderEventId, which a locally-materialised
		// event has no other use for.
		var parsed = CalDavIcs.ParseEvents(ics, $"mail:{messageId}", string.Empty, MaximumEventsPerCalendarPart);
		if (parsed.Count > MaximumEventsPerCalendarPart)
		{
			return;
		}

		if (string.Equals(method, "REQUEST", StringComparison.OrdinalIgnoreCase))
		{
			var verified = await authentication.VerifyAsync(mime, ct);
			if (verified is { IsAuthenticated: true, AuthenticatedAddress: { } address }
				&& parsed.All(dto =>
					dto.Organizer is { } organizer
					&& string.Equals(organizer.Email, address, StringComparison.OrdinalIgnoreCase)))
			{
				foreach (var dto in parsed.Where(e => e.RecurrenceId is null))
				{
					await UpsertAsync(account, dto, ct);
				}
			}
		}
		else if (string.Equals(method, "REPLY", StringComparison.OrdinalIgnoreCase))
		{
			var verified = await authentication.VerifyAsync(mime, ct);
			if (verified is { IsAuthenticated: true, AuthenticatedAddress: { } address })
			{
				foreach (var dto in parsed)
				{
					await ApplyReplyAsync(
						account,
						dto,
						new HashSet<string>([address], StringComparer.OrdinalIgnoreCase),
						requireNewerTimestamp: true,
						ct
					);
				}
			}
		}
	}

	/// <summary>
	/// Applies an unverified iTIP reply only after an explicit user decision. The message remains
	/// visible regardless of authentication, but MIME identity is never sufficient for automatic
	/// state changes because either header can be forged.
	/// </summary>
	public async Task ApplyUnverifiedReplyAsync(Account account, MimeMessage mime, CancellationToken ct = default)
	{
		var part = mime.BodyParts
			.OfType<MimePart>()
			.FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar"));
		if (part?.Content is null)
		{
			return;
		}

		var ics = await CalendarMimeReader.TryReadAsync(part, ct);
		if (ics is null)
		{
			return;
		}
		if (!string.Equals(CalDavIcs.ParseMethod(ics), "REPLY", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		var senders = mime.From.Mailboxes.Select(mailbox => mailbox.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var reply = CalDavIcs.ParseEvents(ics, "mail:manual-reply", string.Empty, MaximumEventsPerCalendarPart).FirstOrDefault();
		if (reply is not null)
		{
			await ApplyReplyAsync(account, reply, senders, requireNewerTimestamp: false, ct);
		}
	}

	/// <summary>Applies an attendee's reply only when the claimed identity matches the sender.</summary>
	private async Task ApplyReplyAsync(
		Account account,
		CalendarEventDto dto,
		IReadOnlySet<string> senders,
		bool requireNewerTimestamp,
		CancellationToken ct
	)
	{
		var respondingAttendee = dto.Attendees.FirstOrDefault();
		if (respondingAttendee is null || !senders.Contains(respondingAttendee.Email))
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

		var target = dto.RecurrenceId is { } recurrenceId
			? candidates.FirstOrDefault(e => e.RecurrenceId == recurrenceId)
			: candidates.FirstOrDefault(e => e.RecurrenceId is null);
		if (target is null)
		{
			return;
		}
		var calendarOwner = await context.Calendars
			.Where(calendar => calendar.Id == target.CalendarId)
			.Select(calendar => calendar.AccountId)
			.Join(context.Accounts, accountId => accountId, owner => owner.Id, (_, owner) => owner.ProviderType)
			.SingleAsync(ct);
		if (calendarOwner == ProviderType.Microsoft365)
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
			if (requireNewerTimestamp
				&& respondingAttendee.ResponseTimestamp is { } newer
				&& (current.ResponseTimestamp is null || newer > current.ResponseTimestamp))
			{
				var timestampUpdated = target.Attendees.ToList();
				timestampUpdated[matchIndex] = current with { ResponseTimestamp = newer };
				target.Attendees = timestampUpdated;
				await context.SaveChangesAsync(ct);
				await events.CalendarEventUpdatedAsync(target.Id);
			}
			return;
		}
		if (requireNewerTimestamp
			&& (dto.Sequence < target.Sequence
				|| respondingAttendee.ResponseTimestamp is null
				|| (current.ResponseTimestamp is { } currentTimestamp
					&& respondingAttendee.ResponseTimestamp <= currentTimestamp)
				|| (current.ResponseTimestamp is null
					&& current.ResponseStatus != ResponseStatus.NeedsAction
					&& dto.Sequence <= target.Sequence)))
		{
			return;
		}

		var updated = target.Attendees.ToList();
		updated[matchIndex] = current with
		{
			ResponseStatus = respondingAttendee.ResponseStatus,
			ResponseTimestamp = respondingAttendee.ResponseTimestamp ?? current.ResponseTimestamp,
		};
		target.Attendees = updated;

		await context.SaveChangesAsync(ct);
		await events.CalendarEventUpdatedAsync(target.Id);
	}

	private async Task UpsertAsync(Account account, CalendarEventDto dto, CancellationToken ct)
	{
		// Native providers already materialise their invites with a provider id and revision.
		// A mail copy of the same invite must never overwrite that canonical state with the
		// synthetic mail: id — doing so would make its next update address a non-existent
		// resource. The message-side banner still parses the MIME directly for immediate RSVP.
		var hasProviderBackedEvent = await context.CalendarEvents.AnyAsync(
			e => e.ICalUid == dto.ICalUid
				&& context.Calendars.Any(c => c.Id == e.CalendarId && c.AccountId == account.Id && !c.IsLocalOnly),
			ct
		);
		if (hasProviderBackedEvent)
		{
			return;
		}

		var localCalendarIds = await context
			.Calendars.Where(calendar => calendar.AccountId == account.Id && calendar.IsLocalOnly)
			.Select(calendar => calendar.Id)
			.ToListAsync(ct);
		var existing = await context.CalendarEvents.FirstOrDefaultAsync(
			e => e.ICalUid == dto.ICalUid && localCalendarIds.Contains(e.CalendarId),
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
