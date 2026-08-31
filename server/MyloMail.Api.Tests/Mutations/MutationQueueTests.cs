using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// Enqueue: sequence assignment and the optimistic desired state that commits with it (§6).
/// </summary>
public sealed class MutationQueueTests
{
	/// <summary>
	/// Desired state is written for the fields the user actually set. A null field means
	/// leave unchanged, and must not acquire a pending value.
	/// </summary>
	[Fact]
	public async Task Setting_flags_records_desired_state_for_the_fields_that_were_set()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var item = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(harness.AccountId, harness.MessageId, new FlagUpdate(IsRead: true, IsFlagged: null))
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var pending = Assert.Single(await context.MessagePendingChanges.ToListAsync());

			Assert.Equal(MessageFlagField.IsRead, pending.Field);
			Assert.True(pending.DesiredValue);
			Assert.Equal(item.Id, pending.MutationItemId);
			Assert.Equal(harness.MessageId, pending.MessageId);
		});
	}

	/// <summary>
	/// Only flag operations have desired state. A move's optimistic effect is a membership
	/// change, not a field value, so inventing a pending flag row for it would show the user
	/// a change they never asked for.
	/// </summary>
	[Fact]
	public async Task A_move_records_no_desired_flag_state()
	{
		await using var harness = await MutationHarness.CreateAsync();

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);

		await harness.UsingAsync(async services =>
			Assert.Empty(
				await services.GetRequiredService<MyloMailDbContext>().MessagePendingChanges.ToListAsync()
			)
		);
	}

	/// <summary>
	/// Ordering is per <c>(AccountId, MessageId)</c>. Sequences shared across messages would
	/// make one message's queue block another's for no reason.
	/// </summary>
	[Fact]
	public async Task Sequences_are_scoped_to_one_message()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var other = await MutationOrderingTests.AddMessageAsync(harness, "second");

		var first = await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);
		var second = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(harness.AccountId, other, new FlagUpdate(true, null))
		);

		// Both are the first mutation for their own message.
		Assert.Equal(1, first.Sequence);
		Assert.Equal(1, second.Sequence);
	}

	/// <summary>
	/// Desired state is keyed per message per field. A pending change for one message must
	/// not be taken over by an enqueue against a different message.
	/// </summary>
	[Fact]
	public async Task Desired_state_for_one_message_is_independent_of_another()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var other = await MutationOrderingTests.AddMessageAsync(harness, "second");

		var first = await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);
		var second = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.SetFlagsAsync(harness.AccountId, other, new FlagUpdate(false, null))
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var pending = await context.MessagePendingChanges.ToListAsync();

			Assert.Equal(2, pending.Count);
			Assert.True(pending.Single(p => p.MessageId == harness.MessageId).DesiredValue);
			Assert.False(pending.Single(p => p.MessageId == other).DesiredValue);
			Assert.Equal(first.Id, pending.Single(p => p.MessageId == harness.MessageId).MutationItemId);
			Assert.Equal(second.Id, pending.Single(p => p.MessageId == other).MutationItemId);
		});
	}

	/// <summary>
	/// Marking a message read and then unread while the first is in flight is ordinary use.
	/// The newest intent owns the field; the older item's revert must then not remove it.
	/// </summary>
	[Fact]
	public async Task The_newest_intent_takes_ownership_of_a_field()
	{
		await using var harness = await MutationHarness.CreateAsync();

		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);
		var second = await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: false);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var pending = Assert.Single(await context.MessagePendingChanges.ToListAsync());

			Assert.False(pending.DesiredValue);
			Assert.Equal(second.Id, pending.MutationItemId);
		});
	}

	/// <summary>
	/// Enqueuing asks for execution, after the commit.
	/// </summary>
	/// <remarks>
	/// Without this the only thing that drains a chain is the startup sweep, so a flag the user
	/// toggled would sit unexecuted until the app was restarted — locally applied, never sent.
	/// </remarks>
	[Fact]
	public async Task Enqueuing_requests_execution_for_the_account()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var item = await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		Assert.Equal([item.AccountId], harness.Dispatcher.Requested);
	}

	/// <summary>An id supplied by the caller is kept, so a caller can correlate what it enqueued.</summary>
	[Fact]
	public async Task A_supplied_id_is_preserved()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var id = Guid.NewGuid();

		var item = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.EnqueueAsync(
					new MutationItem
					{
						Id = id,
						AccountId = harness.AccountId,
						MessageId = harness.MessageId,
						OperationKind = MutationOperationKind.MoveToTrash,
					}
				)
		);

		Assert.Equal(id, item.Id);
	}
}
