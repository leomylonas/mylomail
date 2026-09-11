using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyloMail.Api.Contacts;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
using MyloMail.Api.Tests.Persistence;
using MyloMail.Api.Tests.Sync;

namespace MyloMail.Api.Tests.Contacts;

internal sealed class ContactHarness : IAsyncDisposable
{
	private ServiceProvider services;

	private ContactHarness(TestDatabase database)
	{
		Database = database;
		Provider = new FakeContactProvider();
		Faults = new ScriptedFaultInjector();
		Events = new RecordingHubEvents();
		Jobs = new RecordingJobClient();
		services = Build();
	}

	public TestDatabase Database { get; }
	public FakeContactProvider Provider { get; }
	public ScriptedFaultInjector Faults { get; }
	public RecordingHubEvents Events { get; }
	public RecordingJobClient Jobs { get; }
	public Guid AccountId { get; private set; }

	public static async Task<ContactHarness> CreateAsync()
	{
		var harness = new ContactHarness(new TestDatabase());
		await harness.Database.MigrateAsync();
		await harness.UsingAsync(async provider =>
		{
			var context = provider.GetRequiredService<MyloMailDbContext>();
			var account = new Account
			{
				Id = Guid.NewGuid(),
				DisplayName = "Contacts",
				ProviderType = ProviderType.Microsoft365,
				InitialSyncMode = InitialSyncMode.Full,
			};
			context.Accounts.Add(account);
			await context.SaveChangesAsync();
			harness.AccountId = account.Id;
		});
		return harness;
	}

	public async Task RestartAsync()
	{
		await services.DisposeAsync();
		Faults.Clear();
		services = Build();
	}

	public async Task UsingAsync(Func<IServiceProvider, Task> action)
	{
		await using var scope = services.CreateAsyncScope();
		await action(scope.ServiceProvider);
	}

	public async Task<T> UsingAsync<T>(Func<IServiceProvider, Task<T>> action)
	{
		await using var scope = services.CreateAsyncScope();
		return await action(scope.ServiceProvider);
	}

	private ServiceProvider Build() => new ServiceCollection()
		.AddLogging()
		.AddSingleton<TimeProvider>(TimeProvider.System)
		.AddSingleton<AccountGate>()
		.AddPersistence(Database.Directory)
		.AddMutations()
		.AddSync()
		.AddSingleton<IContactProviderFactory>(new FakeContactProviderFactory(Provider))
		.AddSingleton<IFaultInjector>(Faults)
		.AddSingleton<IHubEvents>(Events)
		.AddSingleton<IBackgroundJobClient>(Jobs)
		.BuildServiceProvider();

	public async ValueTask DisposeAsync()
	{
		await services.DisposeAsync();
		await Database.DisposeAsync();
	}

	private sealed class FakeContactProviderFactory(FakeContactProvider provider) : IContactProviderFactory
	{
		public IContactProvider For(Account account)
		{
			if (provider.FactoryFailure is not { } failure) return provider;
			provider.FactoryFailure = null;
			throw failure;
		}
	}
}

internal sealed class FakeContactProvider : IContactProvider
{
	public ProviderContact Seed(string displayName, params string[] emails)
	{
		var id = $"people/{++sequence}";
		var contact = new ProviderContact(id, $"rev-{sequence}", displayName, emails);
		contacts.Add(id, contact);
		return contact;
	}
	private readonly Dictionary<string, ProviderContact> contacts = [];
	private int sequence;

	public int CreateCalls { get; private set; }
	public int PullCalls { get; private set; }
	public IReadOnlyCollection<ProviderContact> Contacts => contacts.Values;
	public int UpdateCalls { get; private set; }
	public int DeleteCalls { get; private set; }
	public bool RejectWrites { get; set; }
	public bool FailNextPull { get; set; }
	public Exception? PullFailure { get; set; }
	public Exception? FactoryFailure { get; set; }
	public Exception? WriteFailure { get; set; }
	public string? NextCursor { get; set; }
	public bool IsFullSnapshot { get; set; } = true;
	public List<string> DeletedProviderContactIds { get; } = [];


	public ProviderContact Revise(string providerContactId, string displayName, params string[] emails)
	{
		var revised = contacts[providerContactId] with
		{
			Revision = $"rev-{++sequence}",
			DisplayName = displayName,
			Emails = emails,
		};
		contacts[providerContactId] = revised;
		return revised;
	}

	public void Remove(string providerContactId) => contacts.Remove(providerContactId);

	public ProviderContact RenameResource(string providerContactId, string newProviderContactId)
	{
		var renamed = contacts[providerContactId] with
		{
			ProviderContactId = newProviderContactId,
			PreviousProviderContactIds = [providerContactId],
			Revision = $"rev-{++sequence}",
		};
		contacts.Remove(providerContactId);
		contacts.Add(newProviderContactId, renamed);
		return renamed;
	}


	public Task<ContactPullResult> PullAsync(Account account, bool useCursor, CancellationToken ct)
	{
		PullCalls++;
		if (PullFailure is { } failure)
		{
			PullFailure = null;
			throw failure;
		}
		if (FailNextPull)
		{
			FailNextPull = false;
			throw new HttpRequestException("Transient contact pull failure.");
		}
		return Task.FromResult(new ContactPullResult(
			[.. contacts.Values],
			useCursor ? DeletedProviderContactIds : [],
			useCursor ? NextCursor : null,
			!useCursor || IsFullSnapshot
		));
	}

	public Task<ProviderContact> CreateAsync(Account account, ProviderContactWrite contact, CancellationToken ct)
	{
		ThrowWriteFailureIfSet();
		CreateCalls++;
		if (RejectWrites) throw new ProviderContactRejectedException("Rejected.");
		var id = $"people/{++sequence}";
		var created = new ProviderContact(id, $"rev-{sequence}", contact.DisplayName, contact.Emails);
		contacts[id] = created;
		return Task.FromResult(created);
	}

	public Task<ProviderContact> UpdateAsync(
		Account account,
		string providerContactId,
		string? providerContainerId,
		string? expectedRevision,
		ProviderContactWrite contact,
		CancellationToken ct
	)
	{
		ThrowWriteFailureIfSet();
		if (RejectWrites) throw new ProviderContactRejectedException("Rejected.");
		UpdateCalls++;
		var existing = contacts[providerContactId];
		if (expectedRevision is not null && existing.Revision != expectedRevision)
			throw new ProviderConflictException("Contact changed remotely.");
		var updated = existing with
		{
			Revision = $"rev-{++sequence}",
			DisplayName = contact.DisplayName,
			Emails = contact.Emails,
		};
		contacts[providerContactId] = updated;
		return Task.FromResult(updated);
	}

	public Task DeleteAsync(
		Account account,
		string providerContactId,
		string? providerContainerId,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		ThrowWriteFailureIfSet();
		if (RejectWrites) throw new ProviderContactRejectedException("Rejected.");
		DeleteCalls++;
		var existing = contacts[providerContactId];
		if (expectedRevision is not null && existing.Revision != expectedRevision)
			throw new ProviderConflictException("Contact changed remotely.");
		contacts.Remove(providerContactId);
		return Task.CompletedTask;
	}
	private void ThrowWriteFailureIfSet()
	{
		if (WriteFailure is not { } failure) return;
		WriteFailure = null;
		throw failure;
	}
}
