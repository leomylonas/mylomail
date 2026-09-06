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
/// Two-hundred-and-forty-eighth pass: <see cref="ImapMailProvider.Sync"/>'s own <c>ToDto</c>
/// never sets <see cref="MessageDto.Snippet"/> at all — IMAP's ENVELOPE structure carries no
/// preview text, unlike Gmail's <c>Snippet</c>/Graph's <c>BodyPreview</c> — so an IMAP message
/// would show a permanently blank list preview otherwise, the same "ingest-time guess never
/// corrected/announced" shape pass 246/247 already fixed for
/// <see cref="Message.HasNonInlineAttachments"/>.
/// </summary>
public sealed class ContentAcquisitionSnippetTests
{
	[Fact]
	public async Task A_blank_snippet_is_filled_from_the_real_body_and_re_announced()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var messageId = Guid.NewGuid();
		var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		mailbox.Messages[occurrence].RawBytes = MimeBytesWithBody();

		var dbMailboxId = await SeedMailboxAsync(harness);
		await SeedMessageAsync(harness, dbMailboxId, messageId, occurrence, snippet: string.Empty);

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
		Assert.Equal("Please review the attached invoice before Friday.", stored.Snippet);

		var announced = Assert.Single(harness.Events.Updated, m => m.Id == messageId);
		Assert.Equal("Please review the attached invoice before Friday.", announced.Snippet);
	}

	[Fact]
	public async Task A_provider_supplied_snippet_is_never_overwritten()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailbox = harness.Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		var messageId = Guid.NewGuid();
		var occurrence = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);
		mailbox.Messages[occurrence].RawBytes = MimeBytesWithBody();

		var dbMailboxId = await SeedMailboxAsync(harness);
		await SeedMessageAsync(harness, dbMailboxId, messageId, occurrence, snippet: "Gmail's own preview");

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
		Assert.Equal("Gmail's own preview", stored.Snippet);
		Assert.DoesNotContain(harness.Events.Updated, m => m.Id == messageId);
	}

	[Theory]
	[InlineData("Hello   world\n\nnewlines", null, "Hello world newlines")]
	[InlineData(null, "<p>Hi <b>there</b></p>", "Hi there")]
	[InlineData(null, null, "")]
	public void ComputeSnippet_collapses_whitespace_and_strips_html(string? text, string? html, string expected)
	{
		Assert.Equal(expected, ContentAcquisition.ComputeSnippet(text, html));
	}

	[Fact]
	public void ComputeSnippet_falls_back_to_html_when_the_text_part_is_empty_not_null()
	{
		// An empty (not null) plain-text part is a real MIME shape - a stub first alternative
		// alongside the real HTML-only content - and must still reach the HTML fallback.
		Assert.Equal("Hi there", ContentAcquisition.ComputeSnippet("", "<p>Hi <b>there</b></p>"));
	}

	[Fact]
	public void ComputeSnippet_does_not_split_a_surrogate_pair_at_the_truncation_boundary()
	{
		var padding = new string('a', 199);
		// U+1F600 GRINNING FACE - a surrogate pair - placed exactly across the 200-char cut.
		var result = ContentAcquisition.ComputeSnippet(padding + "\U0001F600" + "more text", null);

		Assert.Equal(199, result.Length);
		Assert.Equal(padding, result);
		Assert.False(char.IsHighSurrogate(result[^1]));
		Assert.False(char.IsLowSurrogate(result[^1]));
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
		string providerOccurrenceId,
		string snippet
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
					Snippet = snippet,
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

	private static byte[] MimeBytesWithBody()
	{
		var message = new MimeMessage();
		message.From.Add(MailboxAddress.Parse("author@example.test"));
		message.To.Add(MailboxAddress.Parse("recipient@example.test"));
		message.Subject = "Invoice due";
		message.Body = new TextPart("plain") { Text = "Please review the attached invoice before Friday." };
		using var stream = new MemoryStream();
		message.WriteTo(stream);
		return stream.ToArray();
	}
}
