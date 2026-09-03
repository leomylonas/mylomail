using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
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
	/// Without this, a compose window watching an ambiguous send's status stays on "still
	/// confirming" forever even once reconciliation actually resolves it: nothing told the
	/// renderer the item ever changed (§7). Same missing-announcement bug shape as pass
	/// 58/59, here in the one status writer neither of those passes touched.
	/// </summary>
	[Fact]
	public async Task Resolving_an_ambiguous_send_as_sent_announces_the_new_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SendAsync(harness, item.Id));
		await harness.RestartAsync();

		await MaterialiseInSentAsync(harness, item.StableMessageId);
		harness.Events.Clear();
		await ReconcileAsync(harness);

		var announced = Assert.Single(harness.Events.OutboxStatuses);
		Assert.Equal(item.Id, announced.Id);
		Assert.Equal(OutboxStatus.Sent, announced.Status);
	}

	/// <summary>Same gap as above, on the expiry path rather than the resolved-Sent path.</summary>
	[Fact]
	public async Task An_expired_reconciliation_announces_its_new_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);

		harness.Faults.ArmAt(FaultPoints.AfterDispatchedBeforeProviderCall);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => SendAsync(harness, item.Id));
		await harness.RestartAsync();

		await ReconcileAsync(harness);
		harness.Clock.Advance(SendReconciler.ReconciliationWindow + TimeSpan.FromMinutes(1));
		harness.Events.Clear();
		await ReconcileAsync(harness);

		var announced = Assert.Single(harness.Events.OutboxStatuses);
		Assert.Equal(item.Id, announced.Id);
		Assert.Equal(OutboxStatus.AmbiguousOutcome, announced.Status);
		Assert.NotNull(announced.LastError);
	}

	/// <summary>
	/// Sixty-ninth pass: a successful send never announced its own <c>Sent</c> transition —
	/// the very outcome pass 68's live-status subscription exists to show. Same missing-
	/// announcement shape as the reconciliation paths above, here on <see cref="SendExecutor"/>'s
	/// own terminal-success write.
	/// </summary>
	[Fact]
	public async Task A_successful_send_announces_its_new_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);
		harness.Events.Clear();

		await SendAsync(harness, item.Id);

		var announced = Assert.Single(harness.Events.OutboxStatuses, o => o.Status == OutboxStatus.Sent);
		Assert.Equal(item.Id, announced.Id);
	}

	/// <summary>Same gap as the successful-send case above, on the ambiguous-outcome path.</summary>
	[Fact]
	public async Task A_send_that_throws_announces_its_ambiguous_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);
		harness.Provider.FailSendWith(new InvalidOperationException("connection reset"));
		harness.Events.Clear();

		await SendAsync(harness, item.Id);

		var announced = Assert.Single(harness.Events.OutboxStatuses, o => o.Status == OutboxStatus.AmbiguousOutcome);
		Assert.Equal(item.Id, announced.Id);
	}

	/// <summary>Same gap again, on the categorised-rejection path that returns the item to the queue.</summary>
	[Fact]
	public async Task A_throttled_send_announces_its_scheduled_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await OutboxTests.QueueAsync(harness);
		harness.Provider.FailSendWith(new ProviderThrottledException(TimeSpan.FromMinutes(5), "rate limited"));
		harness.Events.Clear();

		await Assert.ThrowsAsync<ProviderThrottledException>(() => SendAsync(harness, item.Id));

		var announced = Assert.Single(harness.Events.OutboxStatuses, o => o.Status == OutboxStatus.Scheduled);
		Assert.Equal(item.Id, announced.Id);
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
