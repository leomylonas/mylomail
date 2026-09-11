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
		// Collection is the moment GetMessageBody starts reporting the message gone, so a
		// reading pane still polling it has to be told now (§7).
		Assert.Contains(harness.MessageId, harness.Events.Deleted);
	}

	[Fact]
	public async Task Collecting_a_duplicate_parent_rethreads_its_descendants_to_the_surviving_parent()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var survivingParentId = Guid.NewGuid();
		var duplicateParentId = Guid.NewGuid();
		var childId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Messages.AddRange(
				new Message
				{
					Id = survivingParentId,
					AccountId = harness.AccountId,
					MessageIdHeader = "<parent@example.test>",
					ThreadId = $"local:{survivingParentId:N}",
				},
				new Message
				{
					Id = duplicateParentId,
					AccountId = harness.AccountId,
					MessageIdHeader = "<parent@example.test>",
					ThreadId = $"local:{duplicateParentId:N}",
					OrphanedAt = harness.Clock.GetUtcNow() - PastGracePeriod,
				},
				new Message
				{
					Id = childId,
					AccountId = harness.AccountId,
					MessageIdHeader = "<child@example.test>",
					InReplyToHeader = "<parent@example.test>",
					ThreadId = $"local:{childId:N}",
				}
			);
			context.MessageMailboxes.AddRange(
				new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = survivingParentId,
					MailboxId = harness.ArchiveId,
					ProviderOccurrenceId = "surviving-parent",
				},
				new MessageMailbox
				{
					Id = Guid.NewGuid(),
					MessageId = childId,
					MailboxId = harness.ArchiveId,
					ProviderOccurrenceId = "child",
				}
			);
			await context.SaveChangesAsync();
		});

		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.False(await context.Messages.AnyAsync(message => message.Id == duplicateParentId));
			Assert.Equal(
				$"local:{survivingParentId:N}",
				(await context.Messages.SingleAsync(message => message.Id == childId)).ThreadId
			);
		});
		Assert.Contains(harness.Events.Updated, message => message.Id == childId);
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

	/// <summary>
	/// A dispatched attempt has no durable provider outcome. The attempt membership must retain
	/// the canonical message until reconciliation completes, even after its MutationItem became
	/// terminal; otherwise recovery loses the only local identity it can reconcile (§6).
	/// </summary>
	[Fact]
	public async Task A_message_referenced_by_an_unresolved_attempt_is_not_collected()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OrphanTheSeededMessageAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var mutationId = Guid.NewGuid();
			var attemptId = Guid.NewGuid();
			context.MutationItems.Add(new MutationItem
			{
				Id = mutationId,
				AccountId = harness.AccountId,
				MessageId = harness.MessageId,
				State = MutationState.Completed,
				OperationKind = MutationOperationKind.SetFlags,
				Sequence = 1,
			});
			context.MutationExecutionAttempts.Add(new MutationExecutionAttempt
			{
				Id = attemptId,
				AccountId = harness.AccountId,
				Provider = ProviderType.Gmail,
				OperationKind = MutationOperationKind.SetFlags,
				State = MutationAttemptState.Dispatched,
				CreatedAt = DateTimeOffset.UnixEpoch,
			});
			context.MutationExecutionAttemptItems.Add(new MutationExecutionAttemptItem
			{
				AttemptId = attemptId,
				MutationItemId = mutationId,
			});
			await context.SaveChangesAsync();
		});

		await SweepAsync(harness);
		harness.Clock.Advance(PastGracePeriod);
		await SweepAsync(harness);

		await harness.UsingAsync(async services =>
			Assert.True(await services.GetRequiredService<MyloMailDbContext>().Messages.AnyAsync(m => m.Id == harness.MessageId))
		);
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
