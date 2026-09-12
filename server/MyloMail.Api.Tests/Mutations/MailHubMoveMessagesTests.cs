using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// A synthesized mailbox (<see cref="Mailbox.ProviderMailboxId"/> null) has no provider identity
/// to receive a message into. The renderer's own "Move to" menu and drag-and-drop already exclude
/// synthesized targets, but <see cref="MailHub.MoveMessages"/> must reject one too, for any caller
/// that bypasses the renderer.
/// </summary>
public sealed class MailHubMoveMessagesTests
{
	[Fact]
	public async Task Moving_a_message_into_a_synthesized_mailbox_is_rejected_before_enqueue()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var synthesizedId = Guid.NewGuid();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.Mailboxes.Add(
				new Mailbox
				{
					Id = synthesizedId,
					AccountId = harness.AccountId,
					ProviderMailboxId = null,
					Name = "Nested",
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			var ex = await Assert.ThrowsAsync<HubException>(
				() => hub.MoveMessages(harness.AccountId, [harness.MessageId], synthesizedId)
			);
			Assert.Contains("nested label group", ex.Message);
		});

		// Nothing was ever enqueued for it: the message's own occurrence is untouched, and no
		// mutation item exists for it — a genuine pre-enqueue rejection, not a queued item that
		// merely failed downstream.
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.True(
				await context.MessageMailboxes.AnyAsync(o =>
					o.MessageId == harness.MessageId && o.MailboxId == harness.InboxId
				)
			);
			Assert.False(await context.MutationItems.AnyAsync(i => i.MessageId == harness.MessageId));
		});
	}

	[Fact]
	public async Task Moving_a_message_into_a_real_mailbox_still_enqueues_normally()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var result = await harness.UsingAsync(async services =>
		{
			var hub = services.GetRequiredService<MailHub>();
			return await hub.MoveMessages(
				harness.AccountId,
				[harness.MessageId],
				harness.ArchiveId
			);
		});

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var item = await context.MutationItems.SingleAsync(i =>
				i.MessageId == harness.MessageId
			);
			var accepted = Assert.Single(result.Accepted);
			Assert.Equal(harness.MessageId, accepted.MessageId);
			Assert.Equal(item.Id, accepted.MutationItemId);
			Assert.Empty(result.RejectedMessageIds);
		});
	}

	[Fact]
	public async Task A_partial_bulk_enqueue_correlates_accepted_and_rejected_messages()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var missingMessageId = Guid.NewGuid();

		var result = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MailHub>()
				.MoveMessages(
					harness.AccountId,
					[harness.MessageId, missingMessageId],
					harness.ArchiveId
				)
		);

		Assert.Equal(harness.MessageId, Assert.Single(result.Accepted).MessageId);
		Assert.Equal(missingMessageId, Assert.Single(result.RejectedMessageIds));
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(
				result.Accepted[0].MutationItemId,
				(await context.MutationItems.SingleAsync()).Id
			);
		});
	}

	[Fact]
	public async Task Terminal_mutation_lookup_preserves_only_still_pending_optimistic_claims()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var completedId = Guid.NewGuid();
		var pendingId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.MutationItems.AddRange(
				new MutationItem
				{
					Id = completedId,
					AccountId = harness.AccountId,
					MessageId = harness.MessageId,
					Sequence = 1,
					OperationKind = MutationOperationKind.MoveToTrash,
					State = MutationState.Completed,
					CreatedAt = DateTimeOffset.UtcNow,
					CompletedAt = DateTimeOffset.UtcNow,
				},
				new MutationItem
				{
					Id = pendingId,
					AccountId = harness.AccountId,
					MessageId = harness.MessageId,
					Sequence = 2,
					OperationKind = MutationOperationKind.MoveMessage,
					State = MutationState.Pending,
					TargetMailboxId = harness.ArchiveId,
					CreatedAt = DateTimeOffset.UtcNow,
				}
			);
			await context.SaveChangesAsync();
		});

		var terminal = await harness.UsingAsync(services =>
			services
				.GetRequiredService<MailHub>()
				.GetTerminalMutationIds([completedId, pendingId, Guid.NewGuid()])
		);

		Assert.Equal(completedId, Assert.Single(terminal));
	}
}
