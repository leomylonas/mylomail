using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Hubs;

/// <summary>
/// The displayed unread count is a function of durable state, so it must follow every local
/// change at once and must not move when an outcome merely stops being pending.
/// </summary>
public sealed class MailboxUnreadCountTests
{
	private static async Task<(Guid Mailbox, Guid[] Messages)> SeedCompleteMailboxAsync(
		SyncHarness harness,
		SpecialUse use,
		int unread
	)
	{
		var mailboxId = Guid.NewGuid();
		var messages = Enumerable.Range(0, unread).Select(_ => Guid.NewGuid()).ToArray();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.InitialSyncMode = InitialSyncMode.Full;
			context.Mailboxes.Add(new Mailbox
			{
				Id = mailboxId,
				AccountId = harness.Account.Id,
				ProviderMailboxId = "X",
				Name = "X",
				SpecialUse = use,
				// Deliberately stale: the provider has not been asked since before any of this.
				ProviderUnreadCount = 99,
			});
			context.MailboxCoverageStates.Add(new MailboxCoverageState { MailboxId = mailboxId, Status = CoverageStatus.Covered });
			foreach (var id in messages)
			{
				context.Messages.Add(new Message
				{
					Id = id,
					AccountId = harness.Account.Id,
					IsRead = false,
					Occurrences = [new MessageMailbox { Id = Guid.NewGuid(), MailboxId = mailboxId, ProviderOccurrenceId = id.ToString() }],
				});
			}

			await context.SaveChangesAsync();
		});
		return (mailboxId, messages);
	}

	private static Task<int> UnreadAsync(SyncHarness harness, Guid mailboxId) =>
		harness.UsingAsync(async services =>
			(await MailboxSummaryDtoFactory.GetAsync(services.GetRequiredService<MyloMailDbContext>(), mailboxId))!.UnreadCount
		);

	[Fact]
	public async Task A_complete_mailbox_counts_its_own_unread_messages_not_the_providers_stale_snapshot()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, _) = await SeedCompleteMailboxAsync(harness, SpecialUse.Inbox, unread: 3);

		Assert.Equal(3, await UnreadAsync(harness, mailbox));
	}

	[Fact]
	public async Task Marking_read_lowers_the_count_when_queued_and_does_not_move_it_when_the_server_confirms()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, messages) = await SeedCompleteMailboxAsync(harness, SpecialUse.Inbox, unread: 3);

		await harness.UsingAsync(async services =>
			await services.GetRequiredService<MailHub>().SetFlags(harness.Account.Id, [messages[0]], true, null));
		Assert.Equal(2, await UnreadAsync(harness, mailbox));

		// What the executor does on success: the server-known flag changes and the pending row goes.
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			(await context.Messages.SingleAsync(m => m.Id == messages[0])).IsRead = true;
			context.MessagePendingChanges.RemoveRange(context.MessagePendingChanges);
			foreach (var item in context.MutationItems) item.State = MutationState.Completed;
			await context.SaveChangesAsync();
		});
		Assert.Equal(2, await UnreadAsync(harness, mailbox));
	}

	[Theory]
	[InlineData(SpecialUse.Trash)]
	[InlineData(SpecialUse.Junk)]
	public async Task Emptying_a_folder_zeroes_its_count_at_once(SpecialUse use)
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, _) = await SeedCompleteMailboxAsync(harness, use, unread: 4);

		await harness.UsingAsync(async services =>
			await services.GetRequiredService<MailHub>().EmptyMailbox(harness.Account.Id, mailbox));

		Assert.Equal(0, await UnreadAsync(harness, mailbox));
	}

	[Fact]
	public async Task A_message_being_deleted_for_good_stops_counting_even_when_it_carries_several_labels()
	{
		// Gmail keeps an unread trashed message under both TRASH and UNREAD. A permanent delete
		// removes every occurrence, so there is nothing ambiguous to wait for.
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, messages) = await SeedCompleteMailboxAsync(harness, SpecialUse.Trash, unread: 3);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var other = Guid.NewGuid();
			context.Mailboxes.Add(new Mailbox { Id = other, AccountId = harness.Account.Id, ProviderMailboxId = "UNREAD", Name = "UNREAD" });
			context.MessageMailboxes.Add(new MessageMailbox { Id = Guid.NewGuid(), MessageId = messages[0], MailboxId = other, ProviderOccurrenceId = "u" });
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async services =>
			await services.GetRequiredService<MailHub>().DeletePermanently(harness.Account.Id, [messages[0]]));

		Assert.Equal(2, await UnreadAsync(harness, mailbox));
	}

	[Fact]
	public async Task A_message_on_its_way_to_trash_stops_counting_in_its_source_folder()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var (mailbox, messages) = await SeedCompleteMailboxAsync(harness, SpecialUse.Inbox, unread: 3);

		await harness.UsingAsync(async services =>
			await services.GetRequiredService<MailHub>().MoveToTrash(harness.Account.Id, [messages[1]]));

		Assert.Equal(2, await UnreadAsync(harness, mailbox));
	}

	[Theory]
	// Bounded and covered: the provider's figure moves by however far local unread has moved since
	// it was taken (50 then; 3 held locally then, 2 now).
	[InlineData(true, 50, 3, 3, -1, 49)]
	// New unread mail arriving locally after the snapshot raises it too.
	[InlineData(true, 50, 3, 3, 2, 52)]
	// Backfilling: old messages arriving locally never changed the provider's figure, only
	// pending intents do.
	[InlineData(false, 50, null, 40, -1, 49)]
	// Covered but no baseline yet behaves like backfilling.
	[InlineData(true, 50, null, 3, -1, 49)]
	// Never negative.
	[InlineData(false, 1, null, 0, -5, 0)]
	public void A_bounded_mailbox_projects_the_providers_snapshot_over_local_change(
		bool covered,
		int provider,
		int? baseline,
		int serverKnownUnread,
		int pendingDelta,
		int expected
	) =>
		Assert.Equal(
			expected,
			MailboxUnreadProjection.Display(false, covered, provider, baseline, serverKnownUnread, pendingDelta)
		);

	[Fact]
	public void A_mailbox_the_provider_cannot_count_shows_the_local_figure()
	{
		Assert.Equal(5, MailboxUnreadProjection.Display(false, false, null, null, 6, -1));
	}
}
