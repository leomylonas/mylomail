using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>Intent, execution identity and remote uncertainty — §6's remaining three concerns.</summary>
public sealed class MutationExecutionTests
{
	[Fact]
	public async Task A_successful_flag_set_promotes_desired_state_into_server_known_state()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var message = await context.Messages.SingleAsync(m => m.Id == harness.MessageId);
			var item = await context.MutationItems.SingleAsync();

			Assert.True(message.IsRead);
			Assert.Equal(MutationState.Completed, item.State);

			// Desired state exists only while it differs from what the server has confirmed.
			Assert.Empty(await context.MessagePendingChanges.ToListAsync());
		});
	}

	/// <summary>
	/// A move mints a new occurrence id, exactly as an IMAP move changes the UID. The local
	/// occurrence must be re-pointed at it, or the message is unaddressable from then on.
	/// </summary>
	[Fact]
	public async Task A_move_re_establishes_occurrence_identity_at_the_destination()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var before = await OccurrenceIdAsync(harness);

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync(o => o.MessageId == harness.MessageId);

			Assert.Equal(harness.ArchiveId, occurrence.MailboxId);
			Assert.NotEqual(before, occurrence.ProviderOccurrenceId);
		});
	}

	/// <summary>
	/// Execution identity is resolved after preceding mutations settle. Two chained moves are
	/// the case that would break if the occurrence id were captured at enqueue time: the
	/// second would address an id the first destroyed.
	/// </summary>
	[Fact]
	public async Task A_second_move_resolves_the_identity_the_first_one_minted()
	{
		await using var harness = await MutationHarness.CreateAsync();
		harness.Provider.AddMailbox("THIRD");
		var thirdId = await AddMailboxAsync(harness, "THIRD");

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, thirdId)
		);

		await ExecuteAsync(harness);
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync(o => o.MessageId == harness.MessageId);
			Assert.Equal(thirdId, occurrence.MailboxId);
			Assert.All(await context.MutationItems.ToListAsync(), i => Assert.Equal(MutationState.Completed, i.State));
		});
	}

	/// <summary>
	/// A membership-scoped removal whose membership is gone is unsatisfiable. It is cancelled
	/// rather than silently broadened to another occurrence — under Gmail's label model the
	/// message legitimately sits in other mailboxes, and removing the wrong one is a
	/// different, destructive intention.
	/// </summary>
	[Fact]
	public async Task An_unsatisfiable_membership_removal_is_cancelled_not_broadened()
	{
		await using var harness = await MutationHarness.CreateAsync();

		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.RemoveFromMailboxAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);

		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var item = await context.MutationItems.SingleAsync();

			Assert.Equal(MutationState.Cancelled, item.State);

			// The occurrence it was not scoped to is untouched.
			var occurrence = await context.MessageMailboxes.SingleAsync();
			Assert.Equal(harness.InboxId, occurrence.MailboxId);
		});
	}

	/// <summary>
	/// Ordering is not transactionality. A failed move must not cancel a later flag change,
	/// which remains independently satisfiable — cancelling the chain would turn one provider
	/// failure into two abandoned user intentions.
	/// </summary>
	[Fact]
	public async Task A_failed_move_does_not_cancel_a_later_independently_satisfiable_intent()
	{
		await using var harness = await MutationHarness.CreateAsync();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		// The server no longer has the occurrence, so the move fails on its own terms.
		harness.Provider.RemoveMessage(await OccurrenceIdAsync(harness));
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var items = await context.MutationItems.OrderBy(i => i.Sequence).ToListAsync();

			Assert.Equal(MutationState.Failed, items[0].State);
			Assert.Equal(MutationState.Pending, items[1].State);
		});
	}

	/// <summary>
	/// A membership-scoped intent after a failure whose prerequisite is gone is cancelled,
	/// and carries the originating failure so the user is told why.
	/// </summary>
	[Fact]
	public async Task A_later_intent_whose_prerequisite_is_gone_is_cancelled_carrying_the_cause()
	{
		await using var harness = await MutationHarness.CreateAsync();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.RemoveFromMailboxAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);

		harness.Provider.RemoveMessage(await OccurrenceIdAsync(harness));
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var items = await context.MutationItems.OrderBy(i => i.Sequence).ToListAsync();

			Assert.Equal(MutationState.Failed, items[0].State);
			Assert.Equal(MutationState.Cancelled, items[1].State);
			Assert.Equal(items[0].LastError, items[1].LastError);
		});
	}

	internal static async Task ExecuteAsync(MutationHarness harness)
	{
		await harness.UsingAsync(async services =>
		{
			var claimed = await services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-1", TimeSpan.FromMinutes(5), max: 10);

			foreach (var group in claimed.GroupBy(c => (c.OperationKind, c.TargetMailboxId, c.DesiredIsRead, c.DesiredIsFlagged)))
			{
				await services.GetRequiredService<MutationExecutor>().ExecuteAsync(harness.Account, [.. group]);
			}
		});
	}

	private static async Task<string> OccurrenceIdAsync(MutationHarness harness) =>
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.FirstAsync(o => o.MessageId == harness.MessageId);
			return occurrence.ProviderOccurrenceId;
		});

	private static async Task<Guid> AddMailboxAsync(MutationHarness harness, string providerId)
	{
		var id = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = id,
					AccountId = harness.AccountId,
					ProviderMailboxId = providerId,
					Name = providerId,
				}
			);
			await context.SaveChangesAsync();
		});
		return id;
	}
}
