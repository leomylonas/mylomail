using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using Xunit;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// The crash windows from §16's minimum scenario table. These are the primary correctness
/// evidence for the mutation model: the failures are races that ordinary tests do not
/// expose, and every one of them is invisible if the durable boundaries move.
/// </summary>
/// <remarks>
/// Each scenario arms a kill point, runs into it, restarts the host over the same database
/// file, and asserts the externally observable outcome — never internal call order.
/// </remarks>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class MutationCrashWindowTests
{
	/// <summary>Kill point: after the optimistic local commit, before enqueue.</summary>
	/// <remarks>
	/// <para>
	/// Desired state must be either applied or reverted, never orphaned. The mutation and its
	/// pending change commit together, so a crash at this point loses both.
	/// </para>
	/// <para>
	/// The surviving enqueue is the point of the second half. An earlier version asserted only
	/// that the two counts matched, which after a rollback is <c>0 == 0</c> — true even with
	/// pending-change creation deleted outright. Asserting a specific surviving row is what
	/// makes this a test.
	/// </para>
	/// </remarks>
	[Fact]
	public async Task Desired_state_is_never_orphaned_by_a_crash_around_enqueue()
	{
		await using var harness = await MutationHarness.CreateAsync();
		harness.Faults.ArmAt(FaultPoints.AfterOptimisticCommit);

		await Assert.ThrowsAsync<SimulatedCrashException>(
			() => MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true)
		);
		await harness.RestartAsync();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();

			// The crash was inside the transaction, so neither half survived it.
			Assert.Empty(await context.MutationItems.ToListAsync());
			Assert.Empty(await context.MessagePendingChanges.ToListAsync());
		});

		// And the next enqueue, uninterrupted, produces both halves.
		var item = await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var pending = Assert.Single(await context.MessagePendingChanges.ToListAsync());

			Assert.Equal(item.Id, pending.MutationItemId);
			Assert.True(pending.DesiredValue);
			Assert.Equal(MessageFlagField.IsRead, pending.Field);
		});
	}

	/// <summary>Kill point: after enqueue, before the attempt is dispatched.</summary>
	/// <remarks>
	/// Nothing was sent, so the item must re-execute cleanly rather than being treated as
	/// ambiguous. A prepared attempt is not evidence of anything.
	/// </remarks>
	[Fact]
	public async Task A_crash_before_dispatch_re_executes_cleanly()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		harness.Faults.ArmAt(FaultPoints.AfterEnqueueBeforeDispatched);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		await ReleaseLeasesAsync(harness);

		// Nothing was dispatched, so nothing is ambiguous.
		var ambiguous = await AmbiguousItemsAsync(harness);
		Assert.Empty(ambiguous);

		await MutationExecutionTests.ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.True((await context.Messages.SingleAsync(m => m.Id == harness.MessageId)).IsRead);
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
		});
	}

	/// <summary>
	/// Kill point: after the durable <c>Dispatched</c> write, before the provider call. This
	/// is the window the write exists to close.
	/// </summary>
	/// <remarks>
	/// The provider never saw the request, yet the item must still come back ambiguous. That
	/// false positive is the correct answer: locally nothing can distinguish "never sent"
	/// from "sent and the response was lost", and a false negative would silently lose an
	/// outcome or duplicate an externally visible side effect.
	/// </remarks>
	[Fact]
	public async Task A_crash_after_dispatch_is_conservatively_ambiguous()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		var ambiguous = await AmbiguousItemsAsync(harness);
		Assert.Single(ambiguous);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var attempt = await context.MutationExecutionAttempts.SingleAsync();

			Assert.Equal(MutationAttemptState.Dispatched, attempt.State);
			Assert.NotNull(attempt.DispatchedAt);
			Assert.Null(attempt.ResultPersistedAt);
		});
	}

	/// <summary>Kill point: after the provider call, before its results are persisted.</summary>
	/// <remarks>
	/// The server has applied the change and the local database does not know. The item must
	/// be reconciled, not blindly retried — and the attempt, not the item's own state, is
	/// what says so.
	/// </remarks>
	[Fact]
	public async Task A_crash_before_results_persist_reconciles_rather_than_retrying()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		harness.Faults.ArmAt(FaultPoints.AfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		var ambiguous = await AmbiguousItemsAsync(harness);
		Assert.Single(ambiguous);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();

			// The local row still says unread; the server says read. The disagreement is the
			// point — it is discoverable, rather than being resolved by a guess.
			Assert.False((await context.Messages.SingleAsync(m => m.Id == harness.MessageId)).IsRead);
			Assert.Null((await context.MutationExecutionAttempts.SingleAsync()).ResultPersistedAt);
		});
	}

	/// <summary>
	/// A crash during a move leaves an occurrence whose local id is dead on the server. The
	/// item is ambiguous, so recovery reconciles it rather than replaying a move that may
	/// already have happened.
	/// </summary>
	[Fact]
	public async Task A_crash_during_a_move_leaves_occurrence_identity_to_reconciliation()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);

		harness.Faults.ArmAt(FaultPoints.AfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		Assert.Single(await AmbiguousItemsAsync(harness));

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync(o => o.MessageId == harness.MessageId);

			// Still recorded in the source mailbox under an id the server has retired. Only
			// reconciliation can resolve that; a retry would move a message that has already
			// moved.
			Assert.Equal(harness.InboxId, occurrence.MailboxId);
		});
	}

	/// <summary>
	/// The recovery result must differ depending on whether the remote move happened: a lost
	/// response is settled from the destination observation, rather than replaying the move.
	/// </summary>
	[Fact]
	public async Task A_move_applied_before_a_crash_is_reestablished_without_a_second_move()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		harness.Faults.ArmAt(FaultPoints.AfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(services => services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId));

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = Assert.Single(await context.MessageMailboxes.Where(o => o.MessageId == harness.MessageId).ToListAsync());
			Assert.Equal(harness.ArchiveId, occurrence.MailboxId);
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
			Assert.Empty(await services.GetRequiredService<StartupReconciliation>().AmbiguousItemsAsync());
		});
	}

	[Fact]
	public async Task A_move_not_reached_by_the_provider_is_requeued_after_reconciliation()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(services => services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId));

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(MutationState.Pending, (await context.MutationItems.SingleAsync()).State);
			Assert.Equal(harness.InboxId, (await context.MessageMailboxes.SingleAsync()).MailboxId);
		});
	}

	[Fact]
	public async Task A_delete_applied_before_a_crash_is_settled_from_nonexistence()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().DeletePermanentlyAsync(harness.AccountId, harness.MessageId)
		);
		harness.Faults.ArmAt(FaultPoints.AfterProviderCallBeforeResults);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(services => services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId));

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
			Assert.Empty(await context.MessageMailboxes.ToListAsync());
		});
	}

	[Fact]
	public async Task Recovery_does_not_requeue_a_terminal_member_of_an_ambiguous_batch()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var item = await context.MutationItems.SingleAsync();
			item.State = MutationState.Failed;
			context.MutationExecutionAttempts.Add(
				new MutationExecutionAttempt
				{
					Id = Guid.NewGuid(),
					AccountId = harness.AccountId,
					Provider = ProviderType.Gmail,
					OperationKind = MutationOperationKind.SetFlags,
					State = MutationAttemptState.Ambiguous,
					CreatedAt = DateTimeOffset.UnixEpoch,
					Items = [new MutationExecutionAttemptItem { MutationItemId = item.Id }],
				}
			);
			await context.SaveChangesAsync();
		});

		await harness.UsingAsync(services => services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId));

		await harness.UsingAsync(async services =>
			Assert.Equal(MutationState.Failed, (await services.GetRequiredService<MyloMailDbContext>().MutationItems.SingleAsync()).State)
		);
	}

	/// <summary>
	/// Startup reconciliation is the sole recovery mechanism with in-memory job storage, so
	/// it must find every outstanding item — a lease left behind by a dead process included.
	/// </summary>
	[Fact]
	public async Task Startup_reconciliation_finds_work_left_by_a_dead_process()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async services =>
		{
			var reconciliation = services.GetRequiredService<StartupReconciliation>();

			var released = await reconciliation.ReleaseOrphanedLeasesAsync();
			Assert.Equal(1, released);

			var work = await reconciliation.FindAsync();
			Assert.Single(work.NonTerminalMutationChains);
			Assert.Single(work.AmbiguousAttempts);
		});
	}

	private static Task ReleaseLeasesAsync(MutationHarness harness) =>
		harness.UsingAsync(services => services.GetRequiredService<StartupReconciliation>().ReleaseOrphanedLeasesAsync());

	private static async Task<IReadOnlyList<MutationItem>> AmbiguousItemsAsync(MutationHarness harness) =>
		await harness.UsingAsync(services =>
			services.GetRequiredService<StartupReconciliation>().AmbiguousItemsAsync()
		);
}
