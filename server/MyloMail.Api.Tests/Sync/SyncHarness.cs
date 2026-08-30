using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Sync;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
using MyloMail.Api.Tests.Persistence;

namespace MyloMail.Api.Tests.Sync;

/// <summary>
/// A real database and a fake provider of a chosen shape, with a host that can be killed and
/// restarted over the same database file.
/// </summary>
internal sealed class SyncHarness : IAsyncDisposable
{
	private ServiceProvider services;

	private SyncHarness(TestDatabase database, FakeMailProvider provider)
	{
		Database = database;
		Provider = provider;
		Faults = new ScriptedFaultInjector();
		Clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		services = Build();
	}

	public TestDatabase Database { get; }

	public FakeMailProvider Provider { get; }

	public ScriptedFaultInjector Faults { get; }

	public FakeTimeProvider Clock { get; }

	public Account Account { get; private set; } = null!;

	public static async Task<SyncHarness> CreateAsync(ProviderCapabilities capabilities)
	{
		var harness = new SyncHarness(new TestDatabase(), new FakeMailProvider(capabilities));
		await harness.Database.MigrateAsync();
		await harness.SeedAccountAsync(capabilities.Type);
		return harness;
	}

	private ServiceProvider Build() =>
		new ServiceCollection()
			.AddLogging()
			.AddPersistence(Database.Directory)
			.AddSingleton<TimeProvider>(Clock)
			.AddSingleton<IFaultInjector>(Faults)
			.AddSingleton<IMailProviderFactory>(new StubFactory(Provider))
			.AddMutations()
			.AddSync()
			.BuildServiceProvider();

	public async Task RestartAsync()
	{
		await services.DisposeAsync();
		Faults.Clear();
		services = Build();
	}

	public async Task<T> UsingAsync<T>(Func<IServiceProvider, Task<T>> work)
	{
		await using var scope = services.CreateAsyncScope();
		return await work(scope.ServiceProvider);
	}

	public async Task UsingAsync(Func<IServiceProvider, Task> work)
	{
		await using var scope = services.CreateAsyncScope();
		await work(scope.ServiceProvider);
	}

	/// <summary>The account, re-read in the current scope so it is tracked by that context.</summary>
	public Task<Account> AccountInScopeAsync(IServiceProvider scope) =>
		scope.GetRequiredService<MyloMailDbContext>().Accounts.SingleAsync(a => a.Id == Account.Id);

	public Task<Mailbox> MailboxAsync(IServiceProvider scope, string providerMailboxId) =>
		scope
			.GetRequiredService<MyloMailDbContext>()
			.Mailboxes.SingleAsync(m => m.AccountId == Account.Id && m.ProviderMailboxId == providerMailboxId);

	private async Task SeedAccountAsync(ProviderType type)
	{
		await UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			Account = new Account
			{
				Id = Guid.NewGuid(),
				DisplayName = "Fake",
				ProviderType = type,
				InitialSyncMode = InitialSyncMode.Full,
			};
			context.Accounts.Add(Account);
			await context.SaveChangesAsync();
		});
	}

	public async ValueTask DisposeAsync()
	{
		await services.DisposeAsync();
		await Database.DisposeAsync();
	}

	private sealed class StubFactory(IMailProvider provider) : IMailProviderFactory
	{
		public IMailProvider For(ProviderType type) => provider;
	}
}
