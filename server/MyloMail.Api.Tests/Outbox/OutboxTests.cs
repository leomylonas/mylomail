using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Domain;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Outbox;

/// <summary>Undo send, scheduled send, and the cancellation race (§15).</summary>
public sealed class OutboxTests
{
	[Fact]
	public async Task Queueing_waits_for_the_accounts_undo_window()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await SetUndoDelayAsync(harness, 30);

		var item = await QueueAsync(harness);

		Assert.Equal(OutboxStatus.Scheduled, item.Status);
		Assert.Equal(harness.Clock.GetUtcNow().AddSeconds(30), item.ScheduledSendAt);
	}

	/// <summary>
	/// The stable <c>Message-ID</c> exists before the first attempt, because after a crash it
	/// is the only thing that identifies the message in the Sent mailbox.
	/// </summary>
	[Fact]
	public async Task A_stable_message_id_is_generated_before_any_attempt()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var item = await QueueAsync(harness);

		Assert.NotEmpty(item.StableMessageId);
		Assert.StartsWith("<", item.StableMessageId, StringComparison.Ordinal);
		Assert.EndsWith(">", item.StableMessageId, StringComparison.Ordinal);
	}

	[Fact]
	public async Task A_send_still_inside_its_window_can_be_cancelled()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await SetUndoDelayAsync(harness, 30);
		var item = await QueueAsync(harness);

		Assert.True(await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().TryCancelAsync(item.Id)
		));

		Assert.Equal(OutboxStatus.Cancelled, await StatusAsync(harness, item.Id));
	}

	/// <summary>
	/// Cancel and the worker race for the same item, and exactly one wins. A check followed by
	/// a write leaves a window where both see <c>Scheduled</c> and both proceed — one
	/// cancelling a message the other has already begun sending.
	/// </summary>
	[Fact]
	public async Task Exactly_one_of_cancel_and_claim_wins()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await QueueAsync(harness);

		var claimed = await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().TryClaimForSendAsync(item.Id)
		);
		var cancelled = await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().TryCancelAsync(item.Id)
		);

		Assert.True(claimed);
		Assert.False(cancelled);
	}

	/// <summary>
	/// Once sending, cancellation reports "too late" and must not revert to a draft: the
	/// message may already be on its way, and saying otherwise would misrepresent it.
	/// </summary>
	[Fact]
	public async Task Cancelling_a_sending_message_does_not_revert_it()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await QueueAsync(harness);

		await harness.UsingAsync(services =>
			services.GetRequiredService<OutboxService>().TryClaimForSendAsync(item.Id)
		);
		await harness.UsingAsync(services => services.GetRequiredService<OutboxService>().TryCancelAsync(item.Id));

		Assert.Equal(OutboxStatus.Sending, await StatusAsync(harness, item.Id));
	}

	/// <summary>Nothing is dispatched before its scheduled time — that is the whole undo window.</summary>
	[Fact]
	public async Task A_send_is_not_due_until_its_window_closes()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await SetUndoDelayAsync(harness, 30);
		await QueueAsync(harness);

		Assert.Empty(await DueAsync(harness));

		harness.Clock.Advance(TimeSpan.FromSeconds(30));
		Assert.Single(await DueAsync(harness));
	}

	/// <summary>A zero delay means send immediately, which is what most users have configured.</summary>
	[Fact]
	public async Task A_zero_delay_is_due_at_once()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await SetUndoDelayAsync(harness, 0);
		await QueueAsync(harness);

		Assert.Single(await DueAsync(harness));
	}

	/// <summary>
	/// A throttled send returns to the queue rather than becoming ambiguous. The provider
	/// answered — it said no — so the message was not sent, and treating that as an unobserved
	/// outcome would make every rate-limited send something the user has to go and verify.
	/// </summary>
	[Fact]
	public async Task A_send_rejected_by_throttling_returns_to_the_queue()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var item = await QueueAsync(harness);
		harness.Provider.FailSendWith(new ProviderThrottledException(TimeSpan.FromSeconds(30), "slow down"));

		await Assert.ThrowsAsync<ProviderThrottledException>(() =>
			harness.UsingAsync(async services =>
			{
				var context = services.GetRequiredService<MyloMailDbContext>();
				var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
				await services.GetRequiredService<SendExecutor>().SendAsync(account, item.Id);
			})
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var stored = await context.OutboxItems.SingleAsync(o => o.Id == item.Id);
			var attempt = await context.MutationExecutionAttempts.SingleAsync(a => a.OutboxItemId == item.Id);

			Assert.Equal(OutboxStatus.Scheduled, stored.Status);

			// The outcome was observed, so the attempt is closed rather than left unresolved.
			Assert.Equal(MutationAttemptState.Completed, attempt.State);
			Assert.NotNull(attempt.ResultPersistedAt);
		});
	}

	internal static async Task<OutboxItem> QueueAsync(MutationHarness harness, DateTimeOffset? at = null) =>
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
			var draftId = await EnsureDraftAsync(context, harness);

			return await services.GetRequiredService<OutboxService>().QueueAsync(account, draftId, at);
		});

	private static async Task<Guid> EnsureDraftAsync(MyloMailDbContext context, MutationHarness harness)
	{
		var existing = await context.Drafts.FirstOrDefaultAsync();
		if (existing is not null)
		{
			return existing.Id;
		}

		var identity = new SendIdentity
		{
			Id = Guid.NewGuid(),
			AccountId = harness.AccountId,
			DisplayName = "Test",
			EmailAddress = "test@example.org",
			IsDefault = true,
		};
		var draft = new Draft
		{
			Id = Guid.NewGuid(),
			AccountId = harness.AccountId,
			SendIdentityId = identity.Id,
			Subject = "Hello",
			SavedAt = DateTimeOffset.UnixEpoch,
		};

		context.SendIdentities.Add(identity);
		context.Drafts.Add(draft);
		await context.SaveChangesAsync();
		return draft.Id;
	}

	internal static Task SetUndoDelayAsync(MutationHarness harness, int seconds) =>
		harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
			account.UndoSendDelaySeconds = seconds;
			await context.SaveChangesAsync();
		});

	private static Task<IReadOnlyList<OutboxItem>> DueAsync(MutationHarness harness) =>
		harness.UsingAsync(services => services.GetRequiredService<OutboxService>().DueAsync(harness.AccountId));

	private static Task<OutboxStatus> StatusAsync(MutationHarness harness, Guid id) =>
		harness.UsingAsync(async services =>
			(await services.GetRequiredService<MyloMailDbContext>().OutboxItems.SingleAsync(o => o.Id == id)).Status
		);
}
