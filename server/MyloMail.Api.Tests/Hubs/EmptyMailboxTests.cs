using Microsoft.AspNetCore.SignalR;
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

public sealed class EmptyMailboxTests
{
	private static async Task<(Guid Mailbox, Guid[] Messages)> SeedAsync(SyncHarness harness, SpecialUse use, SpecialUse? overrideUse = null, int count = 3)
	{
		var mailboxId = Guid.NewGuid();
		var messages = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox { Id = mailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "X", Name = "X", SpecialUse = use, SpecialUseOverride = overrideUse });
			foreach (var id in messages)
				context.Messages.Add(new Message { Id = id, AccountId = harness.Account.Id, Occurrences = [new MessageMailbox { Id = Guid.NewGuid(), MailboxId = mailboxId, ProviderOccurrenceId = id.ToString() }] });
			await context.SaveChangesAsync();
		});
		return (mailboxId, messages);
	}

	[Theory]
	[InlineData(SpecialUse.Trash)]
	[InlineData(SpecialUse.Junk)]
	public async Task Emptying_trash_or_spam_queues_a_permanent_delete_for_every_message(SpecialUse use)
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, messages) = await SeedAsync(harness, use);

		var result = await harness.UsingAsync(async services =>
			await services.GetRequiredService<MailHub>().EmptyMailbox(harness.Account.Id, mailbox));

		Assert.Equal(messages.Order(), result.Accepted.Select(a => a.MessageId).Order());
		await harness.UsingAsync(async services =>
		{
			var kinds = await services.GetRequiredService<MyloMailDbContext>().MutationItems
				.Where(item => messages.Contains(item.MessageId)).Select(item => item.OperationKind).ToListAsync();
			Assert.Equal(3, kinds.Count);
			Assert.All(kinds, kind => Assert.Equal(MutationOperationKind.DeletePermanently, kind));
		});
	}

	[Theory]
	[InlineData(SpecialUse.Inbox, null)]
	[InlineData(SpecialUse.None, null)]
	[InlineData(SpecialUse.Trash, SpecialUse.Inbox)]
	public async Task Any_other_folder_is_refused_and_nothing_is_queued(SpecialUse use, SpecialUse? overrideUse)
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, _) = await SeedAsync(harness, use, overrideUse);

		await harness.UsingAsync(async services =>
			await Assert.ThrowsAnyAsync<HubException>(() =>
				services.GetRequiredService<MailHub>().EmptyMailbox(harness.Account.Id, mailbox)));

		await harness.UsingAsync(async services =>
			Assert.Empty(await services.GetRequiredService<MyloMailDbContext>().MutationItems.ToListAsync()));
	}

	[Fact]
	public async Task A_folder_the_account_does_not_own_is_refused()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, _) = await SeedAsync(harness, SpecialUse.Trash);

		await harness.UsingAsync(async services =>
			await Assert.ThrowsAnyAsync<HubException>(() =>
				services.GetRequiredService<MailHub>().EmptyMailbox(Guid.NewGuid(), mailbox)));
	}

	[Fact]
	public async Task Raising_the_download_cap_requeues_messages_that_were_only_too_large()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (_, messages) = await SeedAsync(harness, SpecialUse.Inbox, count: 2);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var big = await context.Messages.SingleAsync(m => m.Id == messages[0]);
			big.SizeEstimate = 200L * 1024 * 1024;
			context.MessageContentStates.Add(new MessageContentState { MessageId = messages[0], Status = ContentStatus.Failed, Attempts = 0, LastError = "This message is 200 MB, over this account's 128 MB download limit" });
			context.MessageContentStates.Add(new MessageContentState { MessageId = messages[1], Status = ContentStatus.Failed, Attempts = 5, LastError = "The IMAP server has unexpectedly disconnected." });
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async services =>
		{
			var account = await harness.AccountInScopeAsync(services);
			var hub = services.GetRequiredService<MailHub>();
			await hub.UpdateAccount(new AccountSettingsDto(account.Id, account.DisplayName, account.Color, account.PollIntervalSeconds, account.PollingEnabled, account.UndoSendDelaySeconds, account.NotificationsEnabled, account.InitialSyncMode, account.InitialSyncBoundValue, account.CertificateTrustMode, account.AttachmentSizeLimitOverride, null, 256));
		});

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var states = await context.MessageContentStates.ToDictionaryAsync(s => s.MessageId);
			Assert.Equal(ContentStatus.Queued, states[messages[0]].Status);
			// An unrelated failure is not the cap's to resolve.
			Assert.Equal(ContentStatus.Failed, states[messages[1]].Status);
			Assert.Equal(256, (await context.Accounts.SingleAsync()).MaxMessageDownloadMegabytes);
		});
	}
}
