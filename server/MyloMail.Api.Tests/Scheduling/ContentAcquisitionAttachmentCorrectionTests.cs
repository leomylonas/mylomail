using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Two-hundred-and-forty-seventh pass, closing the gap tracked since pass 246's own
/// invariant-review: when <see cref="ContentAcquisition"/>'s later fetch of the real MIME
/// disagrees with a provider's own ingest-time guess for
/// <see cref="Message.HasNonInlineAttachments"/> (IMAP's BODYSTRUCTURE, Gmail's Payload part
/// tree), the correction is written to the database but nothing tells an already-open message
/// list to refetch it — no <c>MessageUpdated</c> broadcast follows the fix.
/// </summary>
public sealed class ContentAcquisitionAttachmentCorrectionTests
{
	[Fact]
	public async Task Correcting_the_attachment_guess_re_announces_the_message()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var messageId = Guid.NewGuid();
		var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		mailbox.Messages[occurrence].RawBytes = MimeBytesWithAttachment();

		var dbMailboxId = await SeedMailboxAsync(harness);
		await SeedMessageAsync(harness, dbMailboxId, messageId, occurrence);

		await harness.UsingAsync(async scope =>
		{
			var acquisition = scope.GetRequiredService<ContentAcquisition>();
			var account = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			await acquisition.AcquireAsync(account, messageId, CancellationToken.None);
		});

		var stored = await harness.UsingAsync(scope =>
			scope.GetRequiredService<MyloMailDbContext>().Messages.SingleAsync(m => m.Id == messageId)
		);
		Assert.True(stored.HasNonInlineAttachments);

		var announced = Assert.Single(harness.Events.Updated, m => m.Id == messageId);
		Assert.True(announced.HasNonInlineAttachments);
	}

	[Fact]
	public async Task An_unchanged_attachment_guess_is_not_re_announced()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var messageId = Guid.NewGuid();
		var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		mailbox.Messages[occurrence].RawBytes = MimeBytesWithoutAttachment();

		var dbMailboxId = await SeedMailboxAsync(harness);
		await SeedMessageAsync(harness, dbMailboxId, messageId, occurrence);

		await harness.UsingAsync(async scope =>
		{
			var acquisition = scope.GetRequiredService<ContentAcquisition>();
			var account = await scope
				.GetRequiredService<MyloMailDbContext>()
				.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			await acquisition.AcquireAsync(account, messageId, CancellationToken.None);
		});

		Assert.DoesNotContain(harness.Events.Updated, m => m.Id == messageId);
	}

	private static async Task<Guid> SeedMailboxAsync(SyncHarness harness)
	{
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = mailboxId,
					AccountId = harness.Account.Id,
					ProviderMailboxId = "INBOX",
					Name = "Inbox",
					SpecialUse = SpecialUse.Inbox,
				}
			);
			await context.SaveChangesAsync();
		});
		return mailboxId;
	}

	private static async Task SeedMessageAsync(
		SyncHarness harness,
		Guid mailboxId,
		Guid messageId,
		string providerOccurrenceId
	)
	{
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.Account.Id,
					ReceivedAt = DateTimeOffset.UnixEpoch,
					HasNonInlineAttachments = false,
					Occurrences =
					[
						new MessageMailbox
						{
							Id = Guid.NewGuid(),
							MailboxId = mailboxId,
							ProviderOccurrenceId = providerOccurrenceId,
						},
					],
				}
			);
			context.MessageContentStates.Add(
				new MessageContentState
				{
					MessageId = messageId,
					Status = ContentStatus.Queued,
				}
			);
			await context.SaveChangesAsync();
		});
	}

	private static byte[] MimeBytesWithAttachment()
	{
		var builder = new BodyBuilder { HtmlBody = "<p>Body</p>" };
		builder.Attachments.Add("report.pdf", "%PDF-1.4"u8.ToArray());
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.To.Add(MailboxAddress.Parse("recipient@example.test"));
		message.Subject = "Has an attachment";
		message.Body = builder.ToMessageBody();
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}

	private static byte[] MimeBytesWithoutAttachment()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.To.Add(MailboxAddress.Parse("recipient@example.test"));
		message.Subject = "No attachment";
		message.Body = new TextPart("html") { Text = "<p>Body</p>" };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
