using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// Twenty-fourth architecture-review pass: <c>Account.PollingEnabled</c> (§1's "account-level
/// pause switch") was persisted and user-settable but never actually consulted by
/// <see cref="SyncJobs"/> — every poll loop kept running exactly as before, making the
/// settings toggle a complete no-op.
/// </summary>
public sealed class PollingEnabledTests
{
	[Fact]
	public async Task A_polling_disabled_account_neither_syncs_nor_reschedules_its_loop()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		await SetPollingEnabledAsync(harness, false);

		await CalendarAsync(harness);

		// Nothing was scheduled — not even the loop's own reschedule — because the account
		// was never judged runnable in the first place.
		Assert.Empty(await CreatedJobsAsync(harness));
	}

	[Fact]
	public async Task A_polling_enabled_account_reschedules_its_loop_as_before()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);

		await CalendarAsync(harness);

		Assert.NotEmpty(await CreatedJobsAsync(harness));
	}

	/// <summary>
	/// A user can toggle polling off then back on faster than a loop's own poll interval, so
	/// the loop may not have ticked yet and still holds its <see cref="PollRegistry"/> claim
	/// without having actually stopped. Resuming must not force-clear that claim — doing so
	/// (as an earlier version of this fix did, copying <c>StartupScheduler.ResumeAccountAsync</c>
	/// unconditionally) would let a second, concurrent loop start for the same scope, doubling
	/// the poll rate — exactly what <see cref="PollRegistry"/> exists to prevent.
	/// </summary>
	[Fact]
	public async Task Resuming_polling_does_not_start_a_second_loop_for_a_scope_still_claimed()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Graph);
		var mailboxId = await SeedMailboxAsync(harness);

		// Simulates a loop that is still genuinely alive — it hasn't ticked since polling was
		// disabled, so it never released its own slot.
		await ClaimScopeAsync(harness, mailboxId);

		await StartChangeStreamsAsync(harness);

		// The still-alive loop's claim is respected: no second loop was enqueued for it.
		Assert.Empty(await CreatedJobsAsync(harness));
	}

	private static Task<Guid> SeedMailboxAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var mailbox = new MyloMail.Api.Domain.Mailbox
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				ProviderMailboxId = "INBOX",
				Name = "INBOX",
				SpecialUse = MyloMail.Api.Domain.SpecialUse.Inbox,
			};
			context.Mailboxes.Add(mailbox);
			await context.SaveChangesAsync();
			return mailbox.Id;
		});

	private static Task ClaimScopeAsync(SyncHarness harness, Guid mailboxId) =>
		harness.UsingAsync(scope =>
		{
			scope.GetRequiredService<PollRegistry>().TryStart(harness.Account.Id, mailboxId);
			return Task.CompletedTask;
		});

	private static Task StartChangeStreamsAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().StartChangeStreamsAsync(await harness.AccountInScopeAsync(scope))
		);

	private static Task SetPollingEnabledAsync(SyncHarness harness, bool enabled) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.PollingEnabled = enabled;
			await context.SaveChangesAsync();
		});

	private static Task CalendarAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope.GetRequiredService<SyncJobs>().CalendarAsync(harness.Account.Id)
		);

	private static Task<List<Job>> CreatedJobsAsync(SyncHarness harness) =>
		harness.UsingAsync(scope =>
			Task.FromResult(((RecordingJobClient)scope.GetRequiredService<IBackgroundJobClient>()).Created)
		);
}
