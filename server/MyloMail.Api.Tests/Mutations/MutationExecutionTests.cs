using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Fakes;
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
	/// §7 pairs `MessageUpdated` with mutation job completion. Every window holds its own
	/// optimistic projection, and only the one that issued the change hears the confirmation
	/// from its own call.
	/// </summary>
	[Fact]
	public async Task A_confirmed_flag_set_announces_the_message_as_updated()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		await ExecuteAsync(harness);

		var announced = Assert.Single(harness.Events.Updated);
		Assert.Equal(harness.MessageId, announced.Id);
		Assert.True(announced.IsRead);
		Assert.Empty(harness.Events.SyncFailures);
	}

	/// <summary>
	/// An intent the provider rejected is announced as a failure, not as a confirmation: the
	/// server-known state it wanted never happened.
	/// </summary>
	[Fact]
	public async Task A_rejected_flag_set_announces_no_confirmation()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);
		harness.Provider.RemoveMessage(await OccurrenceIdAsync(harness));

		await ExecuteAsync(harness);

		Assert.Empty(harness.Events.Updated);
		Assert.Equal(harness.MessageId, Assert.Single(harness.Events.SyncFailures).MessageId);
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
	/// A confirmed permanent delete is the local-change-confirmed half of §7's
	/// <c>MessageDeleted</c>: the optimistic removal is only news once the provider agreed.
	/// </summary>
	[Fact]
	public async Task A_confirmed_permanent_delete_announces_the_message_as_deleted()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.DeletePermanentlyAsync(harness.AccountId, harness.MessageId)
		);

		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
			Assert.Empty(
				await services.GetRequiredService<MyloMailDbContext>().MessageMailboxes.ToListAsync()
			)
		);
		Assert.Equal(harness.MessageId, Assert.Single(harness.Events.Deleted));
	}

	/// <summary>
	/// A move removes the source occurrence within the same confirmed outcome that creates
	/// the destination one. The message did not disappear, and announcing a deletion here
	/// would empty it out of every open window.
	/// </summary>
	[Fact]
	public async Task A_confirmed_move_announces_no_deletion_for_its_source_occurrence()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);

		await ExecuteAsync(harness);

		Assert.Empty(harness.Events.Deleted);
	}

	/// <summary>
	/// Basic IMAP can confirm a move without returning the destination UID. The source
	/// occurrence must remain locally addressable and the durable attempt must remain open
	/// until provider observation materialises the replacement identity.
	/// </summary>
	[Fact]
	public async Task A_uidless_imap_move_is_completed_only_after_destination_observation()
	{
		await using var harness = await MutationHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);

		await ExecuteAsync(harness);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(harness.InboxId, (await context.MessageMailboxes.SingleAsync()).MailboxId);
			Assert.Equal(MutationState.Leased, (await context.MutationItems.SingleAsync()).State);
			Assert.Equal(
				MutationAttemptState.Ambiguous,
				(await context.MutationExecutionAttempts.SingleAsync()).State
			);
		});

		harness.Events.Clear();
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId)
		);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync();
			Assert.Equal(harness.ArchiveId, occurrence.MailboxId);
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
		});
		// Settling an ambiguous attempt is a job completing just as much as a reported batch
		// outcome is, and it is the point the pending badge stops being true.
		Assert.Equal(harness.MessageId, Assert.Single(harness.Events.Updated).Id);
	}

	/// <summary>
	/// Basic IMAP can crash after COPY creates the Trash occurrence but before the source is
	/// marked deleted and expunged. Recovery must finish only that source deletion; replaying
	/// the whole move can create another Trash copy.
	/// </summary>
	[Fact]
	public async Task A_partial_basic_imap_trash_move_finishes_source_deletion_without_recopying()
	{
		await using var harness = await MutationHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.MoveToTrashAsync(harness.AccountId, harness.MessageId)
		);
		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => MutationExecutionTests.ExecuteAsync(harness));
		await harness.RestartAsync();

		// The only durable fact is Dispatched. This server state is the Basic-tier crash
		// window: COPY happened, while the source UID remains live.
		harness.Provider.SeedMessage("TRASH", harness.MessageId, DateTimeOffset.UnixEpoch);

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId)
		);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var occurrence = await context.MessageMailboxes.SingleAsync();
			Assert.Equal(harness.TrashId, occurrence.MailboxId);
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
		});
	}

	/// <summary>
	/// The effective Trash role can change after dispatch. Recovery must keep the stable
	/// local target captured by that attempt rather than reinterpret an ambiguous call.
	/// </summary>
	[Fact]
	public async Task Trash_recovery_keeps_the_target_resolved_before_dispatch()
	{
		await using var harness = await MutationHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.MoveToTrashAsync(harness.AccountId, harness.MessageId)
		);
		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteAsync(harness));
		await harness.RestartAsync();

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(
				harness.TrashId,
				(await context.MutationExecutionAttemptItems.SingleAsync()).ResolvedTargetMailboxId
			);

			var originalTrash = await context.Mailboxes.SingleAsync(mailbox =>
				mailbox.Id == harness.TrashId
			);
			var replacementTrash = await context.Mailboxes.SingleAsync(mailbox =>
				mailbox.Id == harness.ArchiveId
			);
			originalTrash.SpecialUseOverride = SpecialUse.None;
			replacementTrash.SpecialUseOverride = SpecialUse.Trash;
			await context.SaveChangesAsync();
		});

		// Simulate the Basic-tier partial remote outcome in the originally resolved target.
		harness.Provider.SeedMessage("TRASH", harness.MessageId, DateTimeOffset.UnixEpoch);
		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
			Assert.Equal(harness.TrashId, (await context.MessageMailboxes.SingleAsync()).MailboxId);
		});
	}

	/// <summary>
	/// Message-ID and received-time matching is only a heuristic. A duplicate in another
	/// folder must never be expunged while completing the known source half of a Trash COPY.
	/// </summary>
	[Fact]
	public async Task Partial_trash_recovery_never_deletes_a_heuristic_duplicate()
	{
		await using var harness = await MutationHarness.CreateAsync(
			ProviderShapes.Imap(ImapCapabilityTier.Basic)
		);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.MoveToTrashAsync(harness.AccountId, harness.MessageId)
		);
		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => ExecuteAsync(harness));
		await harness.RestartAsync();

		harness.Provider.SeedMessage("TRASH", harness.MessageId, DateTimeOffset.UnixEpoch);
		harness.Provider.SeedMessage("ARCHIVE", harness.MessageId, DateTimeOffset.UnixEpoch);

		await harness.UsingAsync(services =>
			services.GetRequiredService<MutationReconciler>().ReconcileAsync(harness.AccountId)
		);
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(MutationState.Completed, (await context.MutationItems.SingleAsync()).State);
			Assert.Equal(harness.TrashId, (await context.MessageMailboxes.SingleAsync()).MailboxId);

			var archive = await context.Mailboxes.SingleAsync(mailbox =>
				mailbox.Id == harness.ArchiveId
			);
			var page = await harness.Provider.InitialSyncMailboxAsync(
				harness.Account,
				archive,
				null,
				InitialSyncMode.Full,
				null,
				200,
				CancellationToken.None
			);
			Assert.Contains(
				page.Messages,
				message => message.MessageIdHeader == $"<{harness.MessageId:N}@fake.test>"
			);
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

	/// <summary>
	/// Terminal failure reverts desired state. Leaving it behind shows the user a flag the
	/// server never accepted, indefinitely and with nothing outstanding to correct it.
	/// </summary>
	[Fact]
	public async Task A_failed_flag_set_reverts_its_desired_state()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		harness.Provider.RemoveMessage(await OccurrenceIdAsync(harness));
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();

			Assert.Equal(MutationState.Failed, (await context.MutationItems.SingleAsync()).State);
			Assert.Empty(await context.MessagePendingChanges.ToListAsync());
			Assert.False((await context.Messages.SingleAsync(m => m.Id == harness.MessageId)).IsRead);
		});
	}

	/// <summary>
	/// A cancelled intent reverts its desired state too. Explicit dependency is reserved for
	/// causal ordering that execution-time resolution cannot infer, and this is that case: a
	/// flag change that only makes sense if the move it followed succeeded.
	/// </summary>
	[Fact]
	public async Task A_cancelled_dependent_intent_reverts_its_desired_state()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var move = await harness.UsingAsync(services =>
			services.GetRequiredService<MutationQueue>().MoveAsync(harness.AccountId, harness.MessageId, harness.ArchiveId)
		);
		await harness.UsingAsync(services =>
			services
				.GetRequiredService<MutationQueue>()
				.EnqueueAsync(
					new MutationItem
					{
						AccountId = harness.AccountId,
						MessageId = harness.MessageId,
						OperationKind = MutationOperationKind.SetFlags,
						DesiredIsRead = true,
						DependsOnMutationItemId = move.Id,
					}
				)
		);

		harness.Provider.RemoveMessage(await OccurrenceIdAsync(harness));
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var items = await context.MutationItems.OrderBy(i => i.Sequence).ToListAsync();

			Assert.Equal(MutationState.Failed, items[0].State);
			Assert.Equal(MutationState.Cancelled, items[1].State);
			Assert.Empty(await context.MessagePendingChanges.ToListAsync());
		});
	}

	/// <summary>
	/// An intent cancelled at execution because its target is gone reverts its desired state
	/// too. This is a different path from a chain re-evaluation: nothing failed, the message
	/// simply is not where the operation refers to any more.
	/// </summary>
	[Fact]
	public async Task An_intent_cancelled_at_execution_reverts_its_desired_state()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		// The occurrence is gone locally, so the intent cannot be resolved to anything.
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			context.MessageMailboxes.RemoveRange(await context.MessageMailboxes.ToListAsync());
			await context.SaveChangesAsync();
		});

		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();

			Assert.Equal(MutationState.Cancelled, (await context.MutationItems.SingleAsync()).State);
			Assert.Empty(await context.MessagePendingChanges.ToListAsync());
		});
		// A reverted intent is a change to what every window shows, and the cancellation
		// itself is the only explanation the user gets for the flag springing back.
		Assert.Equal(ErrorCategory.Conflict, Assert.Single(harness.Events.SyncFailures).Category);
		Assert.Equal(harness.MessageId, Assert.Single(harness.Events.Updated).Id);
	}

	/// <summary>
	/// An item the provider did not report on is unresolved, not successful.
	/// </summary>
	/// <remarks>
	/// Partial batch results are the normal case: fifty items in one Graph <c>$batch</c> or
	/// Gmail batch share a dispatch boundary but not an outcome. Marking an unreported item
	/// done would claim knowledge of a server state nobody observed, so it stays in the
	/// dispatched attempt and is reconciled.
	/// </remarks>
	[Fact]
	public async Task An_item_the_batch_did_not_report_stays_unresolved()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		harness.Provider.OmitFromBatchResults(await OccurrenceIdAsync(harness));
		await ExecuteAsync(harness);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var item = await context.MutationItems.SingleAsync();

			Assert.NotEqual(MutationState.Completed, item.State);
			Assert.NotEqual(MutationState.Failed, item.State);

			// Still desired, because nothing has told us it happened or did not.
			Assert.NotEmpty(await context.MessagePendingChanges.ToListAsync());

			// And still reachable from the attempt, which is what routes it to reconciliation.
			Assert.NotEmpty(await services.GetRequiredService<StartupReconciliation>().AmbiguousItemsAsync());
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

	/// <summary>
	/// Hundred-and-fifty-sixth pass: <see cref="MutationFailureDto"/> carried a message's
	/// failure category but not which account it belonged to, so a client-side
	/// reauthenticate/trust-certificate action had no account to act on and always fell back
	/// to a generic refetch. Confirms both new fields — <c>AccountId</c> and the certificate
	/// extensions a rejected-certificate problem carries — are threaded through correctly from
	/// the provider's raw <see cref="MutationProblemDetails"/>.
	/// </summary>
	[Fact]
	public async Task A_failed_mutation_carries_the_accountId_and_certificate_extensions()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await MutationOrderingTests.EnqueueFlagAsync(harness, isRead: true);

		var occurrenceId = await OccurrenceIdAsync(harness);
		var certificateProblem = MyloMail.Api.Security.CertificateTrust.Problem(
			"imap.example.test",
			"deadbeef",
			"Example CA"
		);
		harness.Provider.FailNextBatchItemWith(occurrenceId, certificateProblem);

		await ExecuteAsync(harness);

		var failure = Assert.Single(harness.Events.SyncFailures);
		Assert.Equal(harness.AccountId, failure.AccountId);
		Assert.Equal(harness.MessageId, failure.MessageId);
		Assert.Equal(ErrorCategory.Validation, failure.Category);
		Assert.Equal("imap.example.test", failure.CertificateHostname);
		Assert.Equal("deadbeef", failure.CertificateSha256Fingerprint);
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
