using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Outbox;

/// <summary>
/// §16's <c>Sending</c> outbox scenario: never auto-retried, reconciled against Sent by the
/// stable <c>Message-ID</c>.
/// </summary>
/// <remarks>
/// Send is the operation where getting this wrong is visible to someone other than the user.
/// A replayed flag set converges; a replayed send puts a second copy of a message in a
/// recipient's inbox, and nothing local can distinguish "never sent" from "sent, result
/// lost".
/// </remarks>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class SendCrashWindowTests
{
	/// <summary>Kill point: after the durable <c>Dispatched</c> write, before the provider call.</summary>
	[Fact]
	public async Task A_send_interrupted_after_dispatch_is_never_auto_retried()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SendAsync(harness, item.Id));
		await harness.RestartAsync();

		// Left mid-send. The status is what stops anything picking it up as due work.
		Assert.Equal(OutboxStatus.Sending, await StatusAsync(harness, item.Id));
		Assert.Empty(await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().DueAsync(harness.AccountId)
		));

		// Reconciliation moves it to explicitly ambiguous rather than resending it.
		await ReconcileAsync(harness);
		Assert.Equal(OutboxStatus.AmbiguousOutcome, await StatusAsync(harness, item.Id));
	}

	/// <summary>
	/// The message did arrive in Sent, so reconciliation resolves it — without a second send.
	/// </summary>
	[Fact]
	public async Task An_ambiguous_send_found_in_sent_is_resolved_not_resent()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SendAsync(harness, item.Id));
		await harness.RestartAsync();

		await MaterialiseInSentAsync(harness, item.StableMessageId);
		await ReconcileAsync(harness);

		Assert.Equal(OutboxStatus.Sent, await StatusAsync(harness, item.Id));

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var attempt = await context.MutationExecutionAttempts.SingleAsync(a => a.OutboxItemId == item.Id);

			Assert.Equal(MutationAttemptState.Completed, attempt.State);
			Assert.NotNull(attempt.ResultPersistedAt);
		});
	}

	/// <summary>
	/// The copy has not appeared yet. Neither Gmail nor Graph guarantees it is available
	/// immediately, so the item stays ambiguous inside the window rather than being declared
	/// failed — which is what would produce the duplicate.
	/// </summary>
	[Fact]
	public async Task An_unconfirmed_send_stays_ambiguous_inside_the_window()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SendAsync(harness, item.Id));
		await harness.RestartAsync();

		await ReconcileAsync(harness);
		harness.Clock.Advance(SendReconciler.ReconciliationWindow - TimeSpan.FromMinutes(1));
		await ReconcileAsync(harness);

		Assert.Equal(OutboxStatus.AmbiguousOutcome, await StatusAsync(harness, item.Id));
		Assert.Null(await LastErrorAsync(harness, item.Id));
	}

	/// <summary>
	/// Past the window it is still not resent. Expiry means nobody can tell what happened,
	/// which is a question only the user can answer by looking at their Sent mail.
	/// </summary>
	[Fact]
	public async Task An_expired_reconciliation_asks_the_user_rather_than_resending()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SendAsync(harness, item.Id));
		await harness.RestartAsync();

		await ReconcileAsync(harness);
		harness.Clock.Advance(SendReconciler.ReconciliationWindow + TimeSpan.FromMinutes(1));
		await ReconcileAsync(harness);

		Assert.Equal(OutboxStatus.AmbiguousOutcome, await StatusAsync(harness, item.Id));
		Assert.NotNull(await LastErrorAsync(harness, item.Id));

		// And still nothing queued to send again.
		Assert.Empty(await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().DueAsync(harness.AccountId)
		));
	}

	/// <summary>
	/// Startup reconciliation must find a send left in flight. With in-memory job storage
	/// nothing else knows it existed.
	/// </summary>
	[Fact]
	public async Task Startup_reconciliation_finds_pending_and_unresolved_sends()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var pending = await OutboxTests.QueueAsync(harness);
		await OutboxTests.SetUndoDelayAsync(harness, 300);

		var inFlight = await OutboxTests.QueueAsync(harness);
		await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().TryClaimForSendAsync(inFlight.Id)
		);

		await harness.RestartAsync();

		await harness.UsingAsync(async services =>
		{
			var work = await services.GetRequiredService<MyloMail.Api.Mutations.StartupReconciliation>().FindAsync();

			Assert.Contains(pending.Id, work.PendingSends);
			Assert.Contains(inFlight.Id, work.UnresolvedSends);
		});
	}

	private static Task SendAsync(MutationHarness harness, Guid outboxItemId) =>
		harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
			await services.GetRequiredService<SendExecutor>().SendAsync(account, outboxItemId);
		});

	private static Task ReconcileAsync(MutationHarness harness) =>
		harness.UsingAsync(services =>
			services.GetRequiredService<SendReconciler>().ReconcileAsync(harness.AccountId)
		);

	/// <summary>Stands in for sync surfacing the provider's own Sent copy.</summary>
	private static Task MaterialiseInSentAsync(MutationHarness harness, string stableMessageId) =>
		harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var sent = new Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				ProviderMailboxId = "SENT",
				Name = "Sent",
				SpecialUse = SpecialUse.Sent,
			};
			var message = new Message
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				MessageIdHeader = stableMessageId,
				ReceivedAt = DateTimeOffset.UnixEpoch,
				Occurrences =
				[
					new MessageMailbox
					{
						Id = Guid.NewGuid(),
						MailboxId = sent.Id,
						ProviderOccurrenceId = "sent-1",
					},
				],
			};

			context.Mailboxes.Add(sent);
			context.Messages.Add(message);
			await context.SaveChangesAsync();
		});

	private static Task<OutboxStatus> StatusAsync(MutationHarness harness, Guid id) =>
		harness.UsingAsync(async services =>
			(await services.GetRequiredService<MyloMailDbContext>().OutboxItems.SingleAsync(o => o.Id == id)).Status
		);

	private static Task<string?> LastErrorAsync(MutationHarness harness, Guid id) =>
		harness.UsingAsync(async services =>
			(await services.GetRequiredService<MyloMailDbContext>().OutboxItems.SingleAsync(o => o.Id == id)).LastError
		);
}
