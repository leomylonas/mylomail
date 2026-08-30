using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>Ordering and ownership — §6's first two concerns.</summary>
public sealed class MutationOrderingTests
{
	[Fact]
	public async Task Sequences_are_monotonic_per_message()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var first = await EnqueueFlagAsync(harness, isRead: true);
		var second = await EnqueueFlagAsync(harness, isRead: false);

		Assert.Equal(1, first.Sequence);
		Assert.Equal(2, second.Sequence);
	}

	/// <summary>
	/// Only the lowest non-terminal sequence for a message is eligible. Claiming two
	/// consecutive operations on one message is exactly the race the chain exists to prevent.
	/// </summary>
	[Fact]
	public async Task Only_the_chain_head_is_claimable()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await EnqueueFlagAsync(harness, isRead: true);
		await EnqueueFlagAsync(harness, isRead: false);

		var claimed = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-1", TimeSpan.FromMinutes(5), max: 10)
		);

		var item = Assert.Single(claimed);
		Assert.Equal(1, item.Sequence);
	}

	/// <summary>Different messages proceed concurrently — a chain is not a global lock.</summary>
	[Fact]
	public async Task Heads_of_different_messages_are_claimed_together()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var otherMessageId = await AddMessageAsync(harness, "second");

		await EnqueueFlagAsync(harness, isRead: true);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(harness.AccountId, otherMessageId, new FlagUpdate(true, null))
		);

		var claimed = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-1", TimeSpan.FromMinutes(5), max: 10)
		);

		Assert.Equal(2, claimed.Count);
	}

	/// <summary>
	/// The claim is a conditional update, not a read followed by a decision: a second worker
	/// racing the first must come away with nothing rather than with the same item.
	/// </summary>
	[Fact]
	public async Task A_second_worker_cannot_claim_an_already_leased_item()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await EnqueueFlagAsync(harness, isRead: true);

		var first = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-1", TimeSpan.FromMinutes(5), max: 10)
		);
		var second = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-2", TimeSpan.FromMinutes(5), max: 10)
		);

		Assert.Single(first);
		Assert.Empty(second);
	}

	/// <summary>An expired lease is reclaimable — it says nothing about what the server saw.</summary>
	[Fact]
	public async Task An_expired_lease_is_reclaimable()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await EnqueueFlagAsync(harness, isRead: true);

		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-1", TimeSpan.FromMinutes(5), max: 10)
		);

		harness.Clock.Advance(TimeSpan.FromMinutes(10));

		var reclaimed = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationClaimService>()
				.ClaimAsync(harness.AccountId, "worker-2", TimeSpan.FromMinutes(5), max: 10)
		);

		Assert.Single(reclaimed);
	}

	internal static async Task<MutationItem> EnqueueFlagAsync(MutationHarness harness, bool isRead) =>
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(harness.AccountId, harness.MessageId, new FlagUpdate(isRead, null))
		);

	internal static async Task<Guid> AddMessageAsync(MutationHarness harness, string tag)
	{
		var messageId = Guid.NewGuid();
		var occurrenceId = harness.Provider.SeedMessage("INBOX", messageId, DateTimeOffset.UnixEpoch);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(
				new Message
				{
					Id = messageId,
					AccountId = harness.AccountId,
					ProviderStableId = tag,
					ReceivedAt = DateTimeOffset.UnixEpoch,
					Occurrences =
					[
						new MessageMailbox
						{
							Id = Guid.NewGuid(),
							MailboxId = harness.InboxId,
							ProviderOccurrenceId = occurrenceId,
						},
					],
				}
			);
			await context.SaveChangesAsync();
		});

		return messageId;
	}
}
