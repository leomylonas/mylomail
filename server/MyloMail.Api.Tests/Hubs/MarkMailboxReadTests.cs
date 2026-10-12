using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

public sealed class MarkMailboxReadTests
{
	private static async Task<(Guid Mailbox, Guid Unread, Guid Read, Guid Elsewhere)> SeedAsync(SyncHarness harness)
	{
		var mailboxId = Guid.NewGuid();
		var otherMailboxId = Guid.NewGuid();
		var (unread, read, elsewhere) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(new Mailbox { Id = mailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "A", Name = "A" });
			context.Mailboxes.Add(new Mailbox { Id = otherMailboxId, AccountId = harness.Account.Id, ProviderMailboxId = "B", Name = "B" });
			Message Add(Guid id, Guid mailbox, bool isRead) => new()
			{
				Id = id,
				AccountId = harness.Account.Id,
				IsRead = isRead,
				Occurrences = [new MessageMailbox { Id = Guid.NewGuid(), MailboxId = mailbox, ProviderOccurrenceId = id.ToString() }],
			};
			context.Messages.Add(Add(unread, mailboxId, isRead: false));
			context.Messages.Add(Add(read, mailboxId, isRead: true));
			context.Messages.Add(Add(elsewhere, otherMailboxId, isRead: false));
			await context.SaveChangesAsync();
		});
		return (mailboxId, unread, read, elsewhere);
	}

	[Fact]
	public async Task Only_the_unread_messages_of_that_folder_are_marked_read()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, unread, _, _) = await SeedAsync(harness);

		var queued = await harness.UsingAsync(services =>
			services.GetRequiredService<MailHub>().MarkMailboxRead(harness.Account.Id, mailbox));

		Assert.Equal(1, queued);
		await harness.UsingAsync(async services =>
		{
			var items = await services.GetRequiredService<MyloMailDbContext>().MutationItems.ToListAsync();
			var item = Assert.Single(items);
			Assert.Equal(unread, item.MessageId);
			Assert.Equal(MutationOperationKind.SetFlags, item.OperationKind);
			Assert.True(item.DesiredIsRead);
			Assert.Null(item.DesiredIsFlagged);
		});
	}

	[Fact]
	public async Task Asking_again_does_not_queue_what_is_already_on_its_way_to_read()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, _, _, _) = await SeedAsync(harness);
		var hub = (IServiceProvider services) => services.GetRequiredService<MailHub>();

		Assert.Equal(1, await harness.UsingAsync(services => hub(services).MarkMailboxRead(harness.Account.Id, mailbox)));
		Assert.Equal(0, await harness.UsingAsync(services => hub(services).MarkMailboxRead(harness.Account.Id, mailbox)));
	}

	[Fact]
	public async Task A_folder_the_account_does_not_own_is_refused()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, _, _, _) = await SeedAsync(harness);

		await harness.UsingAsync(async services =>
			await Assert.ThrowsAnyAsync<HubException>(() =>
				services.GetRequiredService<MailHub>().MarkMailboxRead(Guid.NewGuid(), mailbox)));
	}
}
