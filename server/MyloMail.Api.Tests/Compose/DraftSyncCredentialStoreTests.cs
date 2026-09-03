using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Sync;
using Xunit;

namespace MyloMail.Api.Tests.Compose;

/// <summary>
/// Sixty-fourth pass, closing an invariant-review gap in the pass's own fix:
/// <see cref="DraftSyncService.PushAsync"/> called <c>providers.For(account)</c> just like
/// every other job that got a <see cref="CredentialStoreUnavailableException"/> catch —
/// but was missed the first time, so a mid-session credential-store failure during a draft
/// push still fell straight out of the (unguarded, <c>[AutomaticRetry(Attempts = 0)]</c>)
/// Hangfire job with no <see cref="Account.AuthState"/> change and no live announcement.
/// </summary>
public sealed class DraftSyncCredentialStoreTests
{
	[Fact]
	public async Task A_credential_store_failure_during_a_draft_push_announces_a_distinct_status()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var identityId = await SeedIdentityAsync(harness);
		await SeedDraftAsync(harness, identityId);
		harness.Provider.FailDraftPushWith(new CredentialStoreUnavailableException("the keyring is locked"));

		var pushed = await PushAsync(harness);

		Assert.Equal(0, pushed);
		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.Account.Id, announced.Id);
		Assert.Equal(AuthState.CredentialStoreUnavailable, announced.AuthState);

		var account = await harness.UsingAsync(services =>
			services.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync(a => a.Id == harness.Account.Id)
		);
		Assert.Equal(AuthState.CredentialStoreUnavailable, account.AuthState);
	}

	[Fact]
	public async Task A_push_that_succeeds_after_the_store_recovers_clears_the_status()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var identityId = await SeedIdentityAsync(harness);
		await SeedDraftAsync(harness, identityId);
		await harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			account.AuthState = AuthState.CredentialStoreUnavailable;
			account.LastAuthError = "the keyring is locked";
			await context.SaveChangesAsync();
		});
		harness.Events.Clear();

		var pushed = await PushAsync(harness);

		Assert.Equal(1, pushed);
		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.Account.Id, announced.Id);
		Assert.Equal(AuthState.Connected, announced.AuthState);

		var account = await harness.UsingAsync(services =>
			services.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync(a => a.Id == harness.Account.Id)
		);
		Assert.Equal(AuthState.Connected, account.AuthState);
		Assert.Null(account.LastAuthError);
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

	private static Task SeedDraftAsync(SyncHarness harness, Guid identityId) =>
		harness.UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Drafts.Add(
				new Draft
				{
					Id = Guid.NewGuid(),
					AccountId = harness.Account.Id,
					SendIdentityId = identityId,
					Subject = "Subject",
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
