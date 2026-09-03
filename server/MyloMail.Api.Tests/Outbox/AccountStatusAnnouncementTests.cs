using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Outbox;

/// <summary>
/// Fifty-ninth architecture-review pass: background jobs that flip
/// <see cref="Account.AuthState"/> into <see cref="AuthState.NeedsReauth"/> — and
/// <see cref="StartupScheduler.ResumeAccountAsync"/> clearing it back to
/// <see cref="AuthState.Connected"/> — persisted the change but never announced
/// <c>AccountStatusChangedAsync</c> (§7), so an already-open window only learned about it on
/// its next full accounts refresh rather than live, unlike the same transition raised by
/// <c>AccountProvisioningService</c>.
/// </summary>
public sealed class AccountStatusAnnouncementTests
{
	[Fact]
	public async Task An_auth_failure_during_send_announces_the_accounts_new_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OutboxTests.SetUndoDelayAsync(harness, 0);
		var item = await OutboxTests.QueueAsync(harness);
		harness.Provider.FailSendWith(new ProviderAuthenticationException("bad password"));

		await harness.UsingAsync(services => services.GetRequiredService<OutboxJobs>().RunAsync(harness.AccountId));

		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.AccountId, announced.Id);
		Assert.Equal(AuthState.NeedsReauth, announced.AuthState);

		var account = await harness.UsingAsync(services =>
			services.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync(a => a.Id == harness.AccountId)
		);
		Assert.Equal(AuthState.NeedsReauth, account.AuthState);
		Assert.NotNull(item);
	}

	[Fact]
	public async Task Resuming_a_reauthenticated_account_announces_its_new_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
			account.AuthState = AuthState.NeedsReauth;
			account.LastAuthError = "bad password";
			await context.SaveChangesAsync();
		});
		harness.Events.Clear();

		await harness.UsingAsync(services =>
			services.GetRequiredService<StartupScheduler>().ResumeAccountAsync(harness.AccountId)
		);

		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.AccountId, announced.Id);
		Assert.Equal(AuthState.Connected, announced.AuthState);
	}

	/// <summary>
	/// Sixty-fourth pass: a mid-session credential-store failure (a locked OS keyring, a
	/// denied Keychain prompt) fell through the background jobs' generic <c>catch (Exception)</c>
	/// with no <see cref="AuthState"/> change, no <c>LastAuthError</c>, and no live
	/// announcement — the account just silently stopped syncing. Distinct from
	/// <see cref="AuthState.NeedsReauth"/>: the stored credential may well be fine, so
	/// reauthenticating would not help.
	/// </summary>
	[Fact]
	public async Task A_credential_store_failure_during_send_announces_a_distinct_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await OutboxTests.SetUndoDelayAsync(harness, 0);
		var item = await OutboxTests.QueueAsync(harness);
		harness.Provider.FailSendWith(new CredentialStoreUnavailableException("the keyring is locked"));

		await harness.UsingAsync(services => services.GetRequiredService<OutboxJobs>().RunAsync(harness.AccountId));

		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.AccountId, announced.Id);
		Assert.Equal(AuthState.CredentialStoreUnavailable, announced.AuthState);

		var account = await harness.UsingAsync(services =>
			services.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync(a => a.Id == harness.AccountId)
		);
		Assert.Equal(AuthState.CredentialStoreUnavailable, account.AuthState);
		Assert.NotNull(item);
	}

	/// <summary>
	/// Unlike <see cref="AuthState.NeedsReauth"/>, jobs are never gated off while
	/// <see cref="AuthState.CredentialStoreUnavailable"/> — the next scheduled run succeeding
	/// once the OS store is reachable again is the recovery path itself, so it must
	/// self-clear rather than leave a stale banner up.
	/// </summary>
	[Fact]
	public async Task A_run_that_succeeds_after_the_store_recovers_clears_the_status()
	{
		await using var harness = await MutationHarness.CreateAsync();
		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == harness.AccountId);
			account.AuthState = AuthState.CredentialStoreUnavailable;
			account.LastAuthError = "the keyring is locked";
			await context.SaveChangesAsync();
		});
		harness.Events.Clear();
		await OutboxTests.SetUndoDelayAsync(harness, 0);
		await OutboxTests.QueueAsync(harness);

		await harness.UsingAsync(services => services.GetRequiredService<OutboxJobs>().RunAsync(harness.AccountId));

		var announced = Assert.Single(harness.Events.AccountStatuses);
		Assert.Equal(harness.AccountId, announced.Id);
		Assert.Equal(AuthState.Connected, announced.AuthState);

		var account = await harness.UsingAsync(services =>
			services.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync(a => a.Id == harness.AccountId)
		);
		Assert.Equal(AuthState.Connected, account.AuthState);
		Assert.Null(account.LastAuthError);
	}
}
