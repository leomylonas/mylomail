using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// §16's draft-push kill point: one draft's provider call already created a real remote
/// draft — and was recorded in memory — when a later draft in the same batch fails.
/// </summary>
/// <remarks>
/// Twenty-second architecture-review pass: a provider call that returns before its response
/// commits leaves a remote side effect that must remain recoverable. Each result is persisted
/// immediately, so a later failure cannot erase an earlier one. The later attempt remains
/// deliberately ambiguous: a timeout cannot prove its provider call did not succeed, and an
/// empty draft lookup cannot authorize a second create (§6).
/// </remarks>
[Trait("Category", "FaultInjection")]
[Trait("Category", "Deep")]
public sealed class DraftSyncCrashWindowTests
{
	[Fact]
	public async Task An_earlier_drafts_successful_push_survives_a_later_drafts_failure_in_the_same_batch()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var identityId = await SeedIdentityAsync(harness);
		await SeedDraftAsync(harness, identityId, "First draft");
		await SeedDraftAsync(harness, identityId, "Second draft");

		harness.Provider.FailDraftPushWith(new TimeoutException("simulated"), successesBeforeFailure: 1);
		await Assert.ThrowsAsync<TimeoutException>(() => PushAsync(harness));

		// Two provider calls happened: one that succeeded, one that failed. The successful
		// one's remote draft is real and must be durable — not lost just because the batch
		// that contained it didn't finish.
		Assert.Equal(2, harness.Provider.DraftProviderIdsIssued.Count);
		var firstIssuedId = harness.Provider.DraftProviderIdsIssued[0];

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var pushed = await context.Drafts.SingleAsync(d => d.ProviderDraftId == firstIssuedId);
			Assert.NotNull(pushed.PushedAt);
			Assert.Equal(1, await context.Drafts.CountAsync(d => d.PushedAt == null));
		});

		// The earlier success is never replayed. The later timeout is conservatively held for
		// reconciliation rather than converted into an invisible second create.
		await PushAsync(harness);
		Assert.Equal(2, harness.Provider.DraftProviderIdsIssued.Count);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Single(await context.Drafts.Where(d => d.PushedAt != null).ToListAsync());
			var ambiguous = await context.Drafts.SingleAsync(d => d.PushedAt == null);
			Assert.True(ambiguous.SyncConflict);
			Assert.NotNull(ambiguous.PushDispatchedForSavedAt);
		});
	}

	/// <summary>
	/// A provider can accept the first creation and the process can die before its generated
	/// provider id commits. Recovery must reconcile the pre-dispatch stable Message-ID, never
	/// call create a second time and orphan the first remote draft (§6).
	/// </summary>
	[Fact]
	public async Task An_initial_creation_crash_is_reconciled_without_creating_a_second_remote_draft()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var identityId = await SeedIdentityAsync(harness);
		await SeedDraftAsync(harness, identityId, "Only draft");

		harness.Faults.ArmAt(FaultPoints.DraftPushAfterProviderCallBeforeCommit);
		await Assert.ThrowsAsync<SimulatedCrashException>(() => PushAsync(harness));
		Assert.Single(harness.Provider.DraftProviderIdsIssued);

		await harness.RestartAsync();
		await PushAsync(harness);

		Assert.Single(harness.Provider.DraftProviderIdsIssued);
		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync();
			Assert.NotNull(draft.ProviderDraftId);
			Assert.NotNull(draft.PushedAt);
			Assert.Null(draft.PushDispatchedForSavedAt);
		});
	}

	private static async Task<Guid> SeedIdentityAsync(SyncHarness harness)
	{
		var identityId = Guid.NewGuid();
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.SendIdentities.Add(
				new SendIdentity
				{
					Id = identityId,
					AccountId = harness.Account.Id,
					EmailAddress = "author@example.test",
					IsDefault = true,
				}
			);
			await context.SaveChangesAsync();
		});
		return identityId;
	}

	private static Task SeedDraftAsync(SyncHarness harness, Guid identityId, string subject) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Drafts.Add(
				new Draft
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					SendIdentityId = identityId,
					Subject = subject,
					BodyHtml = "<p>Body</p>",
					SavedAt = DateTimeOffset.UnixEpoch,
					StableMessageId = $"<{Guid.NewGuid():N}@mylomail.local>",
				}
			);
			await context.SaveChangesAsync();
		});

	private static Task<int> PushAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftSyncService>().PushAsync(harness.Account.Id)
		);
}
