using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
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
/// Twenty-second architecture-review pass: <see cref="DraftSyncService.PushAsync"/> used to
/// batch every pending draft's provider result under a single <c>SaveChangesAsync</c> after
/// the whole loop. An exception partway through the loop — from a later draft's provider
/// call — meant an earlier draft's already-successful push was never saved either: on retry
/// it looked unpushed and was pushed again, creating an orphaned duplicate the provider has
/// no way to recognise as the same draft (a fresh id is minted whenever
/// <see cref="Draft.ProviderDraftId"/> is null). The fix saves each draft immediately after
/// its own provider call, so an earlier success survives a later failure in the same batch.
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

		// Retrying pushes only the still-pending second draft — the first is not re-sent.
		await PushAsync(harness);
		Assert.Equal(3, harness.Provider.DraftProviderIdsIssued.Count);

		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Assert.Equal(0, await context.Drafts.CountAsync(d => d.PushedAt == null));
			Assert.Equal(2, await context.Drafts.Select(d => d.ProviderDraftId).Distinct().CountAsync());
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
				}
			);
			await context.SaveChangesAsync();
		});

	private static Task<int> PushAsync(SyncHarness harness) =>
		harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftSyncService>().PushAsync(harness.Account.Id)
		);
}
