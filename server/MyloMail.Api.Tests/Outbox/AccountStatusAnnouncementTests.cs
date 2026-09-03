using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
}
