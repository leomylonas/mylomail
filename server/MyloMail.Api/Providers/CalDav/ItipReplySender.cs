using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers.CalDav;

/// <summary>
/// Sends an iTIP <c>REPLY</c> as mail — the mechanism CalDAV/IMAP invites already use (§13
/// Epic 7), and the only mechanism available at all for an event with no real calendar backing
/// it (an <see cref="Calendar.IsLocalOnly"/> pseudo-calendar, materialised from a mailed
/// invite on an account with no CalDAV configured — there is no calendar provider to ask, since
/// none is configured). Factored out of <see cref="CalDavCalendarProvider"/> rather than
/// duplicated, since this never actually touches CalDAV — it only ever calls
/// <see cref="IMailProviderFactory"/>.
/// </summary>
public sealed class ItipReplySender(IMailProviderFactory mail)
{
	public async Task SendAsync(
		Account account,
		CalendarEvent ev,
		InviteResponse response,
		string? comment,
		Address replyingAs,
		CancellationToken ct
	)
	{
		if (ev.Organizer is not { } organizer)
		{
			throw new InvalidOperationException("This event has no organiser to reply to.");
		}

		var status = response switch
		{
			InviteResponse.Accept => ResponseStatus.Accepted,
			InviteResponse.Decline => ResponseStatus.Declined,
			_ => ResponseStatus.Tentative,
		};
		var ics = CalDavIcs.ToReplyIcs(ev, replyingAs, status);
		var verb = response switch
		{
			InviteResponse.Accept => "Accepted",
			InviteResponse.Decline => "Declined",
			_ => "Tentative",
		};

		var draft = new Draft
		{
			AccountId = account.Id,
			FromAddress = replyingAs.Email,
			To = [new Address(organizer.Name, organizer.Email)],
			Subject = $"{verb}: {ev.Title}",
			BodyHtml = System.Net.WebUtility.HtmlEncode(
				comment ?? $"{DisplayName(replyingAs)} has {verb.ToLowerInvariant()} this invitation."
			),
			Attachments =
			[
				new DraftAttachment
				{
					Filename = "invite.ics",
					Content = System.Text.Encoding.UTF8.GetBytes(ics),
					MimeType = "text/calendar; method=REPLY; charset=UTF-8",
				},
			],
		};

		// A reply is not a draft anyone edits or revisits — a fresh Message-ID is exactly
		// right here, unlike a user's own send (§15), which reuses one generated before the
		// first attempt so a crash mid-send can still be reconciled against the Sent mailbox.
		await mail.For(account).SendAsync(account, draft, $"<{Guid.NewGuid()}@mylomail.local>", ct);
	}

	private static string DisplayName(Address address) =>
		address.Name is { Length: > 0 } name ? name : address.Email;
}
