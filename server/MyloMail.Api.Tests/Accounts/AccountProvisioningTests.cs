using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyloMail.Api.Accounts;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;
using MyloMail.Api.Tests.Mutations;
using Xunit;

namespace MyloMail.Api.Tests.Accounts;

public sealed class AccountProvisioningTests
{
	[Fact]
	public async Task Adding_an_account_creates_its_authoritative_send_identity()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var account = await AddAsync(harness, "someone@example.org");

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			var identity = await context.SendIdentities.SingleAsync(i => i.AccountId == account.Id);

			// Account deliberately has no address column; the default identity carries it.
			Assert.True(identity.IsDefault);
			Assert.Equal("someone@example.org", identity.EmailAddress);
		});
	}

	/// <summary>Credentials go to the store, never to a column on the account (§4).</summary>
	[Fact]
	public async Task The_secret_reaches_the_credential_store_and_not_the_account_row()
	{
		await using var harness = await MutationHarness.CreateAsync();

		var account = await AddAsync(harness, "someone@example.org", Secret());

		await harness.UsingAsync(async services =>
		{
			var stored = await services
				.GetRequiredService<ICredentialStore>()
				.RetrieveAsync(account.Id, CancellationToken.None);

			Assert.NotNull(stored);
			Assert.Equal("hunter2"u8.ToArray(), stored!.Data);
		});
	}

	/// <summary>
	/// A rejected account leaves nothing behind — including the credential, which was
	/// necessarily stored before authentication could be attempted.
	/// </summary>
	[Fact]
	public async Task A_rejected_account_leaves_no_orphaned_credential()
	{
		await using var harness = await MutationHarness.CreateAsync();
		harness.Provider.FailAuthentication("wrong password");

		// A spy, because the orphan's id is generated inside the service: asserting the store
		// looks empty afterwards would pass even if nothing were ever written.
		var spy = new RecordingCredentialStore();

		await Assert.ThrowsAnyAsync<Exception>(() =>
			harness.UsingAsync(services =>
				new AccountProvisioningService(
					services.GetRequiredService<MyloMailDbContext>(),
					spy,
					services.GetRequiredService<MyloMail.Api.Providers.IMailProviderFactory>(),
					services.GetRequiredService<MyloMail.Api.Scheduling.StartupScheduler>(),
					services.GetRequiredService<MyloMail.Api.Hubs.IHubEvents>(),
					TimeProvider.System,
					services.GetRequiredService<ILogger<AccountProvisioningService>>()
				).AddAsync(new NewAccount("Test", ProviderType.Gmail, "someone@example.org", null, Secret()))
			)
		);

		var stored = Assert.Single(spy.Stored);
		Assert.Contains(stored, spy.Deleted);

		await harness.UsingAsync(async services =>
			// Only the harness's own account; the rejected one was never committed.
			Assert.Single(await services.GetRequiredService<MyloMailDbContext>().Accounts.ToListAsync())
		);
	}

	private sealed class RecordingCredentialStore : ICredentialStore
	{
		public List<Guid> Stored { get; } = [];

		public List<Guid> Deleted { get; } = [];

		public Task StoreAsync(Guid accountId, CredentialPayload payload, CancellationToken ct)
		{
			Stored.Add(accountId);
			return Task.CompletedTask;
		}

		public Task<CredentialPayload?> RetrieveAsync(Guid accountId, CancellationToken ct) =>
			Task.FromResult<CredentialPayload?>(null);

		public Task DeleteAsync(Guid accountId, CancellationToken ct)
		{
			Deleted.Add(accountId);
			return Task.CompletedTask;
		}
	}

	/// <summary>Removal disables first, so a running job stops before the row disappears (§3).</summary>
	[Fact]
	public async Task Removing_an_account_deletes_it_and_its_credential()
	{
		await using var harness = await MutationHarness.CreateAsync();
		var account = await AddAsync(harness, "someone@example.org", Secret());

		await harness.UsingAsync(services =>
			services.GetRequiredService<AccountProvisioningService>().RemoveAsync(account.Id)
		);

		await harness.UsingAsync(async services =>
		{
			var context = services.GetRequiredService<MyloMailDbContext>();
			Assert.Null(await context.Accounts.FirstOrDefaultAsync(a => a.Id == account.Id));
			Assert.Null(
				await services.GetRequiredService<ICredentialStore>().RetrieveAsync(account.Id, CancellationToken.None)
			);
		});
	}

	private static CredentialPayload Secret() => new("imap-password", "hunter2"u8.ToArray());

	private static Task<Account> AddAsync(MutationHarness harness, string address, CredentialPayload? secret = null) =>
		harness.UsingAsync(services =>
			services
				.GetRequiredService<AccountProvisioningService>()
				.AddAsync(new NewAccount("Test", ProviderType.Gmail, address, null, secret))
		);
}
