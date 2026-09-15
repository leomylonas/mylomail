using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Compose;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task A_definite_pre_create_rejection_clears_the_durable_create_claim(
		bool throttled
	)
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var identityId = await SeedIdentityAsync(harness);
		await SeedDraftAsync(harness, identityId);
		Exception failure = throttled
			? new ProviderThrottledException(TimeSpan.FromSeconds(30), "slow down")
			: new ProviderAuthenticationException("reauthenticate");
		harness.Provider.FailDraftPushWith(failure);

		await Assert.ThrowsAsync(failure.GetType(), () => PushAsync(harness));

		var draft = await harness.UsingAsync(services =>
			services.GetRequiredService<MyloMailDbContext>().Drafts.SingleAsync()
		);
		Assert.Null(draft.ProviderDraftId);
		Assert.Null(draft.PushDispatchedForSavedAt);
		Assert.Null(draft.PushedAt);
	}

	[Fact]
	public async Task Draft_job_records_authentication_failure_and_leaves_the_dirty_draft_resumable()
	{
		await using var harness = await SyncHarness.CreateAsync(ProviderShapes.Gmail);
		var identityId = await SeedIdentityAsync(harness);
		await SeedDraftAsync(harness, identityId);
		harness.Provider.FailDraftPushWith(new ProviderAuthenticationException("reauthenticate"));
		harness.Events.Clear();

		await harness.UsingAsync(scope =>
			scope.GetRequiredService<DraftJobs>().PushAsync(harness.Account.Id)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.Account.Id);
			var draft = await context.Drafts.SingleAsync();
			Assert.Equal(AuthState.NeedsReauth, account.AuthState);
			Assert.Null(draft.ProviderDraftId);
			Assert.Null(draft.PushDispatchedForSavedAt);
			Assert.Null(draft.PushedAt);
		});
		Assert.Equal(
			AuthState.NeedsReauth,
			Assert.Single(harness.Events.AccountStatuses).AuthState
		);
	}

	[Fact]
	public async Task Missing_provider_configuration_leaves_an_unclaimed_dirty_draft()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var draftId = Guid.NewGuid();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var identity = new SendIdentity
			{
				Id = Guid.NewGuid(),
				AccountId = harness.AccountId,
				EmailAddress = "author@example.test",
				IsDefault = true,
			};
			context.SendIdentities.Add(identity);
			context.Drafts.Add(
				new Draft
				{
					Id = draftId,
					AccountId = harness.AccountId,
					SendIdentityId = identity.Id,
					Subject = "Subject",
					BodyHtml = "<p>Body</p>",
					SavedAt = DateTimeOffset.UnixEpoch,
				}
			);
			await context.SaveChangesAsync();
		});
		harness.FailNextProviderResolutionWith = new ProviderNotConfiguredException(
			ProviderType.Gmail,
			"Providers:Gmail:ClientId"
		);
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services.GetRequiredService<DraftJobs>().PushAsync(harness.AccountId)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
			var draft = await context.Drafts.SingleAsync(d => d.Id == draftId);
			Assert.Equal(AuthState.Error, account.AuthState);
			Assert.Null(draft.ProviderDraftId);
			Assert.Null(draft.PushDispatchedForSavedAt);
			Assert.Null(draft.PushedAt);
		});
		Assert.Equal(AuthState.Error, Assert.Single(harness.Events.AccountStatuses).AuthState);
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
