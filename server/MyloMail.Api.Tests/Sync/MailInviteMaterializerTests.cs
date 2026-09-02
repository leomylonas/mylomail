using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
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
			await new MailInviteMaterializer(context, new RecordingHubEvents())
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
				await new MailInviteMaterializer(context, new RecordingHubEvents())
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
			await new MailInviteMaterializer(context, new RecordingHubEvents())
				.MaterializeFromMessageAsync(account, messageId, mime, default);
			return true;
		});

		await UsingAsync(database, async context =>
		{
			Assert.False(await context.Calendars.AnyAsync(c => c.AccountId == accountId));
			return true;
		});
	}

	private static Task Materialize(TestDatabase database, Guid accountId, Guid messageId, MimeMessage mime) =>
		UsingAsync(database, async context =>
		{
			var account = await context.Accounts.SingleAsync(a => a.Id == accountId);
			await new MailInviteMaterializer(context, new RecordingHubEvents())
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

	private static MimeMessage InviteMime(string uid, string title, int sequence, string method = "REQUEST")
	{
		var ics =
			$"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:{method}\r\n"
			+ $"BEGIN:VEVENT\r\nUID:{uid}\r\nSEQUENCE:{sequence}\r\nSUMMARY:{title}\r\n"
			+ "DTSTART:20260601T090000Z\r\nDTEND:20260601T093000Z\r\n"
			+ "ORGANIZER;CN=Jane Doe:mailto:jane@example.org\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

		var mime = new MimeMessage();
		mime.From.Add(MailboxAddress.Parse("jane@example.org"));
		var body = new BodyBuilder { TextBody = "You're invited." };
		body.Attachments.Add(
			"invite.ics",
			System.Text.Encoding.UTF8.GetBytes(ics),
			ContentType.Parse("text/calendar")
		);
		mime.Body = body.ToMessageBody();
		return mime;
	}
}
