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
/// §7 pairs <c>SyncProgress</c> with content-index pages as well as backfill pages. Indexing
/// runs long after coverage reports complete, so the two producers are distinguished by kind
/// rather than sharing one set of numbers.
/// </summary>
public sealed class ContentIndexProgressTests
{
	[Fact]
	public async Task Indexing_a_message_reports_the_mailbox_aggregate_as_content_progress()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var providerMailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var mailboxId = await SeedMailboxAsync(harness);
		var first = Guid.NewGuid();
		var second = Guid.NewGuid();
		foreach (var messageId in new[] { first, second })
		{
			var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
			providerMailbox.Messages[occurrence].RawBytes = MimeBytes();
			await SeedMessageAsync(harness, mailboxId, messageId, occurrence);
		}

		await AcquireAsync(harness, first);

		var afterFirst = Assert.Single(harness.Events.Progress);
		Assert.Equal(SyncProgressKind.Content, afterFirst.Kind);
		Assert.Equal(mailboxId, afterFirst.MailboxId);
		Assert.Equal(1, afterFirst.MessagesFetched);
		Assert.Equal(2, afterFirst.EstimatedTotal);
		// Coverage owns the coverage status; a mailbox-scoped content status would be a
		// fiction under Gmail's canonical model.
		Assert.Null(afterFirst.Status);

		harness.Events.Clear();
		await AcquireAsync(harness, second);

		var afterSecond = Assert.Single(harness.Events.Progress);
		Assert.Equal(2, afterSecond.MessagesFetched);
		Assert.Equal(2, afterSecond.EstimatedTotal);
	}

	/// <summary>
	/// Progress is reported for every mailbox the message belongs to: under Gmail's canonical
	/// model one message is in several labels at once, and each of their aggregates moved.
	/// </summary>
	[Fact]
	public async Task Indexing_reports_every_mailbox_the_message_belongs_to()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var providerMailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var inboxId = await SeedMailboxAsync(harness);
		var receiptsId = await SeedMailboxAsync(harness, "RECEIPTS", SpecialUse.None);
		var messageId = Guid.NewGuid();
		var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		providerMailbox.Messages[occurrence].RawBytes = MimeBytes();
		await SeedMessageAsync(harness, inboxId, messageId, occurrence, alsoIn: receiptsId);

		await AcquireAsync(harness, messageId);

		Assert.Equal(
			new[] { inboxId, receiptsId }.Order(),
			harness.Events.Progress.Select(progress => progress.MailboxId).Order()
		);
		Assert.All(
			harness.Events.Progress,
			progress =>
			{
				Assert.Equal(SyncProgressKind.Content, progress.Kind);
				Assert.Equal(1, progress.MessagesFetched);
				Assert.Equal(1, progress.EstimatedTotal);
			}
		);
	}

	/// <summary>A failed fetch has indexed nothing, so it reports no progress.</summary>
	[Fact]
	public async Task A_failed_fetch_reports_no_content_progress()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var mailboxId = await SeedMailboxAsync(harness);
		var messageId = Guid.NewGuid();
		var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		await SeedMessageAsync(harness, mailboxId, messageId, occurrence);
		harness.Provider.FailFetchRawMessageWith(new InvalidOperationException("Unreadable."));

		await Assert.ThrowsAsync<InvalidOperationException>(() => AcquireAsync(harness, messageId));

		Assert.Empty(harness.Events.Progress);
	}

	private static Task AcquireAsync(SyncHarness harness, Guid messageId) =>
		harness.UsingAsync(async scope =>
			await scope
				.GetRequiredService<ContentAcquisition>()
				.AcquireAsync(await harness.AccountInScopeAsync(scope), messageId)
		);

	private static async Task<Guid> SeedMailboxAsync(
		SyncHarness harness,
		string providerMailboxId = "INBOX",
		SpecialUse specialUse = SpecialUse.Inbox
	)
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
					ProviderMailboxId = providerMailboxId,
					Name = providerMailboxId,
					SpecialUse = specialUse,
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
		string providerOccurrenceId,
		Guid? alsoIn = null
	)
	{
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var occurrences = new List<MessageMailbox>
			{
				new()
				{
					Id = Guid.NewGuid(),
					MailboxId = mailboxId,
					ProviderOccurrenceId = providerOccurrenceId,
				},
			};
			if (alsoIn is Guid second)
			{
				occurrences.Add(
					new MessageMailbox
					{
						Id = Guid.NewGuid(),
						MailboxId = second,
						ProviderOccurrenceId = providerOccurrenceId,
					}
				);
			}

			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.Account.Id,
					ReceivedAt = DateTimeOffset.UnixEpoch,
					Snippet = "seeded",
					Occurrences = occurrences,
				}
			);
			context.MessageContentStates.Add(
				new MessageContentState { MessageId = messageId, Status = ContentStatus.Queued }
			);
			await context.SaveChangesAsync();
		});
	}

	private static byte[] MimeBytes()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.To.Add(MailboxAddress.Parse("recipient@example.test"));
		message.Subject = "Indexed";
		message.Body = new TextPart("plain") { Text = "Body text." };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
