using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// Background failures are durable state; the user has to be able to see which messages and
/// folders they affect and what the error was, and to retry a download that was given up on.
/// </summary>
public sealed class ProblemReportTests
{
	[Fact]
	public async Task A_message_whose_download_was_abandoned_is_reported_with_its_details_and_error()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var messageId = await SeedFailedMessageAsync(harness, "Invoice", attempts: 5, error: "The IMAP server has unexpectedly disconnected.");

		var problem = await harness.UsingAsync(async services =>
			Assert.Single(await services.GetRequiredService<MailHub>().GetProblems()));

		Assert.Equal(ProblemKind.MessageDownload, problem.Kind);
		Assert.Equal(messageId, problem.MessageId);
		Assert.Equal("Invoice", problem.Subject);
		Assert.Equal("Ada <ada@example.test>", problem.From);
		Assert.Equal("INBOX", problem.MailboxName);
		Assert.Equal("The IMAP server has unexpectedly disconnected.", problem.Detail);
		Assert.Equal(5, problem.Attempts);
		Assert.True(problem.CanRetry);
	}

	[Fact]
	public async Task A_message_over_the_accounts_limit_is_reported_as_too_large_with_the_current_limit_in_its_text()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var messageId = await SeedFailedMessageAsync(harness, "Huge", attempts: 5, error: "Provider content exceeds the 67108864-byte limit.");
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			(await context.Messages.SingleAsync(m => m.Id == messageId)).SizeEstimate = 300L * 1024 * 1024;
			await context.SaveChangesAsync();
		});

		var problem = await harness.UsingAsync(async services =>
			Assert.Single(await services.GetRequiredService<MailHub>().GetProblems()));

		Assert.Equal(ProblemKind.MessageTooLarge, problem.Kind);
		Assert.False(problem.CanRetry);
		Assert.Equal(harness.Account.Id, problem.AccountId);
		Assert.Contains("300 MB", problem.Detail);
		Assert.Contains("128 MB", problem.Detail);
		Assert.DoesNotContain("67108864", problem.Detail);
	}

	[Fact]
	public async Task A_message_still_waiting_to_download_for_the_first_time_is_not_a_problem()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await SeedFailedMessageAsync(harness, "Pending", attempts: 1, error: null, status: ContentStatus.Queued);

		var problems = await harness.UsingAsync(async services => await services.GetRequiredService<MailHub>().GetProblems());

		Assert.Empty(problems);
	}

	[Fact]
	public async Task A_message_queued_for_another_try_is_reported_as_retrying_not_as_a_problem()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await SeedFailedMessageAsync(harness, "Again", attempts: 0, error: "boom", status: ContentStatus.Queued);

		var item = await harness.UsingAsync(async services =>
			Assert.Single(await services.GetRequiredService<MailHub>().GetProblems()));

		Assert.Equal(ProblemKind.MessageRetrying, item.Kind);
		Assert.Equal("Again", item.Subject);
	}

	[Fact]
	public async Task A_folder_with_a_recorded_sync_error_is_reported_with_that_error()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var mailboxId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox { Id = mailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "Tax", Name = "Tax" });
			context.IntegrityReconciliationStates.Add(new IntegrityReconciliationState { MailboxId = mailboxId, LastError = "UIDVALIDITY changed twice." });
			await context.SaveChangesAsync();
		});

		var problem = await harness.UsingAsync(async services =>
			Assert.Single(await services.GetRequiredService<MailHub>().GetProblems()));

		Assert.Equal(ProblemKind.FolderSync, problem.Kind);
		Assert.Equal(mailboxId, problem.MailboxId);
		Assert.Equal("Tax", problem.MailboxName);
		Assert.Equal("UIDVALIDITY changed twice.", problem.Detail);
		Assert.False(problem.CanRetry);
	}

	[Fact]
	public async Task Retrying_requeues_only_abandoned_messages_with_a_fresh_attempt_budget()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var failed = await SeedFailedMessageAsync(harness, "Failed", attempts: 5, error: "boom");
		var indexed = await SeedFailedMessageAsync(harness, "Fine", attempts: 1, error: null, status: ContentStatus.Indexed);

		var requeued = await harness.UsingAsync(async services =>
			await services.GetRequiredService<MailHub>().RetryFailedDownloads([failed, indexed]));

		Assert.Equal(1, requeued);
		await harness.UsingAsync(async services =>
		{
			var states = await services.GetRequiredService<MyloMailDbContext>().MessageContentStates.ToDictionaryAsync(s => s.MessageId);
			Assert.Equal(ContentStatus.Queued, states[failed].Status);
			Assert.Equal(0, states[failed].Attempts);
			// Kept, so the message reads as "being retried" rather than as a fresh, unexplained one.
			Assert.Equal("boom", states[failed].LastError);
			Assert.Equal(ContentStatus.Indexed, states[indexed].Status);
			Assert.Equal(1, states[indexed].Attempts);
		});
	}

	private static async Task<Guid> SeedFailedMessageAsync(
		SyncHarness harness,
		string subject,
		int attempts,
		string? error,
		ContentStatus status = ContentStatus.Failed
	)
	{
		var messageId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var mailbox = await context.Mailboxes.FirstOrDefaultAsync(m => m.Name == "INBOX");
			if (mailbox is null)
			{
				mailbox = new Mailbox { Id = Guid.NewGuid(), AccountId = harness.Account.Id, ProviderMailboxId = "INBOX", Name = "INBOX", SpecialUse = SpecialUse.Inbox };
				context.Mailboxes.Add(mailbox);
			}
			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.Account.Id,
					Subject = subject,
					From = [new Address("Ada", "ada@example.test")],
					ReceivedAt = DateTimeOffset.UnixEpoch,
					Snippet = "seeded",
					Occurrences = [new MessageMailbox { Id = Guid.NewGuid(), MailboxId = mailbox.Id, ProviderOccurrenceId = messageId.ToString() }],
				}
			);
			context.MessageContentStates.Add(new MessageContentState { MessageId = messageId, Status = status, Attempts = attempts, LastError = error });
			await context.SaveChangesAsync();
		});
		return messageId;
	}

	[Fact]
	public async Task The_content_queue_counts_ready_waiting_and_fetching_but_not_abandoned_messages()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		await SeedFailedMessageAsync(harness, "a", 0, null, ContentStatus.Indexed);
		await SeedFailedMessageAsync(harness, "b", 0, null, ContentStatus.Indexed);
		await SeedFailedMessageAsync(harness, "c", 0, null, ContentStatus.Queued);
		await SeedFailedMessageAsync(harness, "d", 0, null, ContentStatus.NotFetched);
		await SeedFailedMessageAsync(harness, "e", 0, null, ContentStatus.Fetching);
		await SeedFailedMessageAsync(harness, "f", 5, "boom", ContentStatus.Failed);

		var queue = await harness.UsingAsync(async services => await services.GetRequiredService<MailHub>().GetContentQueue());

		Assert.Equal(new ContentQueueDto(2, 2, 1), queue);
	}
}
