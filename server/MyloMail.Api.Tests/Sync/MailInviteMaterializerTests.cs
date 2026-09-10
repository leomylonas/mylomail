using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Security;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// The "from message" half of Epic 7's RSVP requirement (§1, §13): a received invite becomes
/// something a user can respond to, without needing any calendar already configured.
/// </summary>
public sealed class MailInviteMaterializerTests
{
	[Fact]
	public async Task A_received_invite_materialises_an_event_under_a_new_local_only_calendar()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		var mime = InviteMime("uid-1", "Standup", sequence: 0);

		await UsingAsync(database, async context =>
		{
			var account = await context.Accounts.SingleAsync(a => a.Id == accountId);
			await new MailInviteMaterializer(context, new RecordingHubEvents(), new AlwaysAuthenticated())
				.MaterializeFromMessageAsync(account, messageId, mime, default);
			return true;
		});

		await UsingAsync(database, async context =>
		{
			var ev = await context.CalendarEvents.SingleAsync(e => e.ICalUid == "uid-1");
			Assert.Equal("Standup", ev.Title);

			var calendar = await context.Calendars.SingleAsync(c => c.Id == ev.CalendarId);
			Assert.True(calendar.IsLocalOnly);
			Assert.False(calendar.IsDefault);
			return true;
		});
	}

	[Fact]
	public async Task An_oversized_calendar_part_is_ignored_without_materialising_an_event()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		var oversized = InviteMime("oversized", "Standup", sequence: 0)
			.ToString()
			+ new string('x', CalendarMimeReader.MaximumDecodedBytes);
		var mime = WrapAsMessage(oversized);

		await Materialize(database, accountId, messageId, mime);

		await UsingAsync(database, async context =>
		{
			Assert.False(await context.CalendarEvents.AnyAsync());
			return true;
		});
	}

	[Fact]
	public async Task A_received_invite_never_overwrites_a_provider_backed_event_with_the_same_uid()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		var calendarId = Guid.NewGuid();
		var eventId = Guid.NewGuid();
		await UsingAsync(database, async context =>
		{
			context.Calendars.Add(new Calendar
			{
				Id = calendarId,
				AccountId = accountId,
				ProviderCalendarId = "native-calendar",
				Name = "Native",
			});
			context.CalendarEvents.Add(new CalendarEvent
			{
				Id = eventId,
				CalendarId = calendarId,
				ProviderEventId = "native-event",
				ICalUid = "native-uid",
				ProviderRevision = "native-revision",
				Title = "Native title",
			});
			await context.SaveChangesAsync();
			return true;
		});

		await Materialize(database, accountId, messageId, InviteMime("native-uid", "Mail title", sequence: 2));

		await UsingAsync(database, async context =>
		{
			var existing = await context.CalendarEvents.SingleAsync(calendarEvent => calendarEvent.Id == eventId);
			Assert.Equal("native-event", existing.ProviderEventId);
			Assert.Equal("native-revision", existing.ProviderRevision);
			Assert.Equal("Native title", existing.Title);
			Assert.Equal(1, await context.CalendarEvents.CountAsync(calendarEvent => calendarEvent.ICalUid == "native-uid"));
			return true;
		});
	}

	[Fact]
	public async Task Materialising_the_same_invite_twice_does_not_duplicate_the_event()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		var mime = InviteMime("uid-2", "Standup", sequence: 0);

		for (var i = 0; i < 2; i++)
		{
			await UsingAsync(database, async context =>
			{
				var account = await context.Accounts.SingleAsync(a => a.Id == accountId);
				await new MailInviteMaterializer(context, new RecordingHubEvents(), new AlwaysAuthenticated())
					.MaterializeFromMessageAsync(account, messageId, mime, default);
				return true;
			});
		}

		await UsingAsync(database, async context =>
		{
			Assert.Equal(1, await context.CalendarEvents.CountAsync(e => e.ICalUid == "uid-2"));
			// The second pass reuses the same local-only calendar rather than creating another.
			Assert.Equal(1, await context.Calendars.CountAsync(c => c.AccountId == accountId));
			return true;
		});
	}

	[Fact]
	public async Task A_higher_sequence_update_replaces_the_title_and_a_lower_one_is_ignored()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await Materialize(database, accountId, messageId, InviteMime("uid-3", "Standup", sequence: 1));
		await Materialize(database, accountId, messageId, InviteMime("uid-3", "Standup (moved)", sequence: 2));

		await UsingAsync(database, async context =>
		{
			Assert.Equal("Standup (moved)", (await context.CalendarEvents.SingleAsync(e => e.ICalUid == "uid-3")).Title);
			return true;
		});

		// A stale, out-of-order redelivery of the earlier version must not regress it.
		await Materialize(database, accountId, messageId, InviteMime("uid-3", "Standup (stale)", sequence: 0));

		await UsingAsync(database, async context =>
		{
			Assert.Equal("Standup (moved)", (await context.CalendarEvents.SingleAsync(e => e.ICalUid == "uid-3")).Title);
			return true;
		});
	}

	[Fact]
	public async Task A_cancellation_or_reply_method_is_not_materialised()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await Materialize(database, accountId, messageId, InviteMime("uid-4", "Standup", sequence: 0, method: "CANCEL"));

		await UsingAsync(database, async context =>
		{
			Assert.False(await context.CalendarEvents.AnyAsync(e => e.ICalUid == "uid-4"));
			return true;
		});
	}

	[Fact]
	public async Task A_message_with_no_calendar_part_is_left_alone()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		var mime = new MimeMessage();
		mime.From.Add(MailboxAddress.Parse("sender@example.org"));
		mime.Body = new TextPart("plain") { Text = "just an ordinary message" };

		await UsingAsync(database, async context =>
		{
			var account = await context.Accounts.SingleAsync(a => a.Id == accountId);
			await new MailInviteMaterializer(context, new RecordingHubEvents(), new AlwaysAuthenticated())
				.MaterializeFromMessageAsync(account, messageId, mime, default);
			return true;
		});

		await UsingAsync(database, async context =>
		{
			Assert.False(await context.Calendars.AnyAsync(c => c.AccountId == accountId));
			return true;
		});
	}

	[Fact]
	public async Task A_reply_updates_the_matching_attendees_response_status()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await Materialize(
			database,
			accountId,
			messageId,
			InviteMime("uid-5", "Standup", sequence: 0, attendee: "bob@example.org")
		);
		await Materialize(database, accountId, messageId, ReplyMime("uid-5", "bob@example.org", "ACCEPTED"));

		await UsingAsync(database, async context =>
		{
			var ev = await context.CalendarEvents.SingleAsync(e => e.ICalUid == "uid-5");
			var bob = Assert.Single(ev.Attendees, a => a.Email == "bob@example.org");
			Assert.Equal(ResponseStatus.Accepted, bob.ResponseStatus);
			return true;
		});
	}

	[Fact]
	public async Task An_older_authenticated_reply_does_not_overwrite_a_newer_response()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		await Materialize(
			database,
			accountId,
			messageId,
			InviteMime("uid-reply-order", "Standup", sequence: 0, attendee: "bob@example.org")
		);
		await Materialize(
			database,
			accountId,
			messageId,
			ReplyMime("uid-reply-order", "bob@example.org", "ACCEPTED", "20260601T090000Z")
		);
		await Materialize(
			database,
			accountId,
			messageId,
			ReplyMime("uid-reply-order", "bob@example.org", "DECLINED", "20260601T080000Z")
		);

		await UsingAsync(database, async context =>
		{
			var attendee = Assert.Single(
				(await context.CalendarEvents.SingleAsync(eventRow => eventRow.ICalUid == "uid-reply-order")).Attendees
			);
			Assert.Equal(ResponseStatus.Accepted, attendee.ResponseStatus);
			return true;
		});
	}

	[Fact]
	public async Task An_authenticated_reply_to_an_older_invite_revision_is_ignored()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		await Materialize(
			database,
			accountId,
			messageId,
			InviteMime("uid-reply-sequence", "Standup", sequence: 2, attendee: "bob@example.org")
		);
		await Materialize(
			database,
			accountId,
			messageId,
			ReplyMime("uid-reply-sequence", "bob@example.org", "ACCEPTED", "20260601T090000Z", sequence: 1)
		);

		await UsingAsync(database, async context =>
		{
			var attendee = Assert.Single(
				(await context.CalendarEvents.SingleAsync(eventRow => eventRow.ICalUid == "uid-reply-sequence")).Attendees
			);
			Assert.Equal(ResponseStatus.NeedsAction, attendee.ResponseStatus);
			return true;
		});
	}

	[Fact]
	public async Task An_unverified_reply_requires_explicit_user_acceptance_before_it_changes_an_attendee()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		await Materialize(
			database,
			accountId,
			messageId,
			InviteMime("uid-unverified-reply", "Standup", sequence: 0, attendee: "bob@example.org")
		);
		var reply = ReplyMime("uid-unverified-reply", "bob@example.org", "ACCEPTED");

		await UsingAsync(database, async context =>
		{
			var account = await context.Accounts.SingleAsync(row => row.Id == accountId);
			var materializer = new MailInviteMaterializer(context, new RecordingHubEvents(), new NeverAuthenticated());
			await materializer.MaterializeFromMessageAsync(account, messageId, reply, default);

			var attendee = Assert.Single(
				(await context.CalendarEvents.SingleAsync(eventRow => eventRow.ICalUid == "uid-unverified-reply")).Attendees
			);
			Assert.Equal(ResponseStatus.NeedsAction, attendee.ResponseStatus);

			await materializer.ApplyUnverifiedReplyAsync(account, reply, default);
			return true;
		});

		await UsingAsync(database, async context =>
		{
			var attendee = Assert.Single(
				(await context.CalendarEvents.SingleAsync(eventRow => eventRow.ICalUid == "uid-unverified-reply")).Attendees
			);
			Assert.Equal(ResponseStatus.Accepted, attendee.ResponseStatus);
			return true;
		});
	}

	[Fact]
	public async Task A_reply_claiming_another_attendee_is_ignored()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);
		await Materialize(database, accountId, messageId, InviteMime("uid-spoofed-reply", "Standup", sequence: 0, attendee: "bob@example.org"));
		var forged = ReplyMime("uid-spoofed-reply", "bob@example.org", "ACCEPTED");
		forged.From.Clear();
		forged.From.Add(MailboxAddress.Parse("attacker@example.org"));
		await Materialize(database, accountId, messageId, forged);

		await UsingAsync(database, async context =>
		{
			var attendee = Assert.Single((await context.CalendarEvents.SingleAsync(e => e.ICalUid == "uid-spoofed-reply")).Attendees);
			Assert.Equal(ResponseStatus.NeedsAction, attendee.ResponseStatus);
			return true;
		});
	}

	[Fact]
	public async Task A_reply_from_an_address_not_on_the_attendee_list_is_ignored()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		await Materialize(
			database,
			accountId,
			messageId,
			InviteMime("uid-6", "Standup", sequence: 0, attendee: "bob@example.org")
		);
		await Materialize(database, accountId, messageId, ReplyMime("uid-6", "stranger@example.org", "ACCEPTED"));

		await UsingAsync(database, async context =>
		{
			var ev = await context.CalendarEvents.SingleAsync(e => e.ICalUid == "uid-6");
			var bob = Assert.Single(ev.Attendees);
			Assert.Equal("bob@example.org", bob.Email);
			Assert.Equal(ResponseStatus.NeedsAction, bob.ResponseStatus);
			return true;
		});
	}

	[Fact]
	public async Task A_reply_to_an_unknown_event_uid_is_ignored_without_error()
	{
		await using var database = new TestDatabase();
		await database.MigrateAsync();
		var (accountId, messageId) = await SeedAsync(database);

		// No matching invite was ever materialised for this UID.
		await Materialize(database, accountId, messageId, ReplyMime("uid-does-not-exist", "bob@example.org", "ACCEPTED"));

		await UsingAsync(database, async context =>
		{
			Assert.False(await context.CalendarEvents.AnyAsync());
			return true;
		});
	}

	private sealed class AlwaysAuthenticated : IIncomingMailAuthentication
	{
		public Task<MailAuthenticationResult> VerifyAsync(MimeMessage message, CancellationToken ct) =>
			Task.FromResult(new MailAuthenticationResult(true, message.From.Mailboxes.Single().Address));
	}

	private sealed class NeverAuthenticated : IIncomingMailAuthentication
	{
		public Task<MailAuthenticationResult> VerifyAsync(MimeMessage message, CancellationToken ct) =>
			Task.FromResult(MailAuthenticationResult.Unverified);
	}

	private static Task Materialize(TestDatabase database, Guid accountId, Guid messageId, MimeMessage mime) =>
		UsingAsync(database, async context =>
		{
			var account = await context.Accounts.SingleAsync(a => a.Id == accountId);
			await new MailInviteMaterializer(context, new RecordingHubEvents(), new AlwaysAuthenticated())
				.MaterializeFromMessageAsync(account, messageId, mime, default);
			return true;
		});

	private static async Task<T> UsingAsync<T>(TestDatabase database, Func<MyloMailDbContext, Task<T>> work)
	{
		await using var scope = database.CreateScope();
		return await work(scope.ServiceProvider.GetRequiredService<MyloMailDbContext>());
	}

	private static async Task<(Guid AccountId, Guid MessageId)> SeedAsync(TestDatabase database) =>
		await UsingAsync(database, async context =>
		{
			var account = new Account { Id = Guid.NewGuid(), ProviderType = ProviderType.Imap };
			var messageId = Guid.NewGuid();
			context.Accounts.Add(account);
			context.Messages.Add(
				new Message { Id = messageId, AccountId = account.Id, ReceivedAt = DateTimeOffset.UtcNow }
			);
			await context.SaveChangesAsync();
			return (account.Id, messageId);
		});

	private static MimeMessage InviteMime(
		string uid,
		string title,
		int sequence,
		string method = "REQUEST",
		string? attendee = null
	)
	{
		var attendeeLine = attendee is null ? "" : $"ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:{attendee}\r\n";
		var ics =
			$"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:{method}\r\n"
			+ $"BEGIN:VEVENT\r\nUID:{uid}\r\nSEQUENCE:{sequence}\r\nSUMMARY:{title}\r\n"
			+ "DTSTART:20260601T090000Z\r\nDTEND:20260601T093000Z\r\n"
			+ $"ORGANIZER;CN=Jane Doe:mailto:jane@example.org\r\n{attendeeLine}END:VEVENT\r\nEND:VCALENDAR\r\n";

		return WrapAsMessage(ics);
	}

	private static MimeMessage ReplyMime(
		string uid,
		string replyingAttendee,
		string partstat,
		string timestamp = "20260601T080000Z",
		int sequence = 0
	)
	{
		var ics =
			"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:REPLY\r\n"
			+ $"BEGIN:VEVENT\r\nUID:{uid}\r\nSEQUENCE:{sequence}\r\nDTSTAMP:{timestamp}\r\nSUMMARY:Standup\r\n"
			+ "DTSTART:20260601T090000Z\r\nDTEND:20260601T093000Z\r\n"
			+ "ORGANIZER;CN=Jane Doe:mailto:jane@example.org\r\n"
			+ $"ATTENDEE;PARTSTAT={partstat}:mailto:{replyingAttendee}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		var mime = WrapAsMessage(ics);
		mime.From.Clear();
		mime.From.Add(MailboxAddress.Parse(replyingAttendee));
		return mime;
	}

	private static MimeMessage WrapAsMessage(string ics)
	{
		var mime = new MimeMessage();
		mime.From.Add(MailboxAddress.Parse("jane@example.org"));
		var body = new BodyBuilder { TextBody = "Calendar update." };
		body.Attachments.Add(
			"invite.ics",
			System.Text.Encoding.UTF8.GetBytes(ics),
			ContentType.Parse("text/calendar")
		);
		mime.Body = body.ToMessageBody();
		return mime;
	}
}
