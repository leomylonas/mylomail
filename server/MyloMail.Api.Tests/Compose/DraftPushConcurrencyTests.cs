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
/// An update replaces the server's copy and invalidates the revision it was based on. Two pushes of
/// one draft in flight at once (an autosave and a Send, say) both start from the same revision:
/// the second then finds its revision gone and flags a conflict nobody caused, and Send refuses
/// until the user resolves it.
/// </summary>
public sealed class DraftPushConcurrencyTests
{
	[Fact]
	public async Task Two_pushes_of_one_draft_at_once_do_not_conflict_with_each_other()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		harness.Provider.EnforceDraftRevisions = true;
		var draftId = await SeedAlreadyPushedAndEditedAsync(harness);

		// The first push is held inside the provider while the second one starts.
		var release = new TaskCompletionSource();
		var entered = new TaskCompletionSource();
		harness.Provider.DuringDraftPushAsync = async () =>
		{
			entered.TrySetResult();
			await release.Task;
		};
		var first = harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftSyncService>().PushAsync(harness.Account.Id)
		);
		await entered.Task;
		var second = harness.UsingAsync(async scope =>
			await scope.GetRequiredService<DraftSyncService>().PushAsync(harness.Account.Id)
		);
		await Task.Delay(300);
		release.SetResult();
		await Task.WhenAll(first, second);

		await harness.UsingAsync(async scope =>
		{
			var draft = await scope.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync(d => d.Id == draftId);
			Assert.False(draft.SyncConflict);
			Assert.True(draft.PushedAt >= draft.SavedAt);
		});
	}

	private static async Task<Guid> SeedAlreadyPushedAndEditedAsync(SyncHarness harness)
	{
		var draftId = Guid.NewGuid();
		var stable = "<stable@mylomail.local>";
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var identity = new SendIdentity
			{
				Id = Guid.NewGuid(),
				AccountId = harness.Account.Id,
				EmailAddress = "author@example.test",
				IsDefault = true,
			};
			context.SendIdentities.Add(identity);
			context.Drafts.Add(
				new Draft
				{
					Id = draftId,
					AccountId = harness.Account.Id,
					SendIdentityId = identity.Id,
					Subject = "Subject",
					BodyHtml = "<p>Body</p>",
					To = [new Address(null, "recipient@example.test")],
					StableMessageId = stable,
					SavedAt = DateTimeOffset.UnixEpoch.AddMinutes(5),
					PushedAt = DateTimeOffset.UnixEpoch,
					ProviderDraftId = "existing",
					ProviderMessageId = "existing",
					ProviderRevision = "rev-1",
				}
			);
			await context.SaveChangesAsync();
		});
		// The server holds the revision the draft was last pushed at.
		harness.Provider.SeedDraft(stable, "existing", "rev-1");
		return draftId;
	}
}
