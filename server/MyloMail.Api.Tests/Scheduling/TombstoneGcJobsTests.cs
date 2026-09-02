using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Tombstone collection (§6): a message with no mailbox membership is reclaimed only once its
/// grace period (§3) has elapsed and nothing else references it.
/// </summary>
public sealed class TombstoneGcJobsTests
{
	private static readonly TimeSpan PastGracePeriod = TimeSpan.FromMinutes(31);

	[Fact]
	public async Task An_orphaned_message_is_not_collected_before_its_grace_period_elapses()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);

		// First sweep only starts the grace period — it must not collect on the same pass a
		// message is first noticed orphaned, or a Graph move's second delta (§3) would always
		// lose the race.
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync(m => m.Id == harness.MessageId);
			Assert.NotNull(message.OrphanedAt);
		});
	}

	[Fact]
	public async Task An_orphaned_message_with_no_references_is_collected_once_its_grace_period_elapses()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);

		await SweepAsync(harness);
		harness.Clock.Advance(PastGracePeriod);
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.False(await context.Messages.AnyAsync(m => m.Id == harness.MessageId));
		});
	}

	[Fact]
	public async Task A_message_referenced_by_a_live_mutation_is_not_collected()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.MutationItems.Add(
				new MutationItem
				{
					Id = Guid.NewGuid(),
					AccountId = harness.AccountId,
					MessageId = harness.MessageId,
					State = MutationState.Pending,
					OperationKind = MutationOperationKind.SetFlags,
					Sequence = 1,
				}
			);
			await context.SaveChangesAsync();
		});

		await SweepAsync(harness);
		harness.Clock.Advance(PastGracePeriod);
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.Messages.AnyAsync(m => m.Id == harness.MessageId));
		});
	}

	[Fact]
	public async Task A_message_with_an_undelivered_notification_is_not_collected()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.NotificationRecords.Add(
				new NotificationRecord
				{
					Id = Guid.NewGuid(),
					AccountId = harness.AccountId,
					MessageId = harness.MessageId,
					CreatedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		});

		await SweepAsync(harness);
		harness.Clock.Advance(PastGracePeriod);
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.Messages.AnyAsync(m => m.Id == harness.MessageId));
		});
	}

	[Fact]
	public async Task A_message_a_draft_is_replying_to_is_not_collected()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var identity = new SendIdentity
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				EmailAddress = "test@example.com",
				IsDefault = true,
			};
			context.SendIdentities.Add(identity);
			context.Drafts.Add(
				new Draft
				{
					Id = Guid.NewGuid(),
					AccountId = harness.AccountId,
					SendIdentityId = identity.Id,
					InReplyToMessageId = harness.MessageId,
				}
			);
			await context.SaveChangesAsync();
		});

		await SweepAsync(harness);
		harness.Clock.Advance(PastGracePeriod);
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.Messages.AnyAsync(m => m.Id == harness.MessageId));
		});
	}

	[Fact]
	public async Task A_message_that_regains_membership_before_its_grace_period_elapses_is_not_collected()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);
		await SweepAsync(harness);

		// The late destination-addition delta §3 describes: membership returns before the
		// grace period is up.
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.MessageMailboxes.Add(
				new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = harness.MessageId,
					MailboxId = harness.ArchiveId,
					ProviderOccurrenceId = "regained",
				}
			);
			var message = await context.Messages.SingleAsync(m => m.Id == harness.MessageId);
			message.OrphanedAt = null;
			await context.SaveChangesAsync();
		});

		harness.Clock.Advance(PastGracePeriod);
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.True(await context.Messages.AnyAsync(m => m.Id == harness.MessageId));
		});
	}

	private static Task SweepAsync(MutationHarness harness) =>
		harness.UsingAsync(services =>
			services.GetRequiredService<TombstoneGcJobs>().SweepAsync(harness.AccountId, default)
		);

	/// <summary>Drops the seeded message's only mailbox membership, making it a tombstone.</summary>
	private static async Task OrphanTheSeededMessageAsync(MutationHarness harness) =>
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.MessageMailboxes.RemoveRange(
				context.MessageMailboxes.Where(o => o.MessageId == harness.MessageId)
			);
			await context.SaveChangesAsync();
		});
}
