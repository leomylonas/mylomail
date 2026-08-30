using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Persistence;

namespace MyloMail.Api.Tests.Mutations;

/// <summary>
/// A real database, a fake provider, and a fault injector that can kill at a named point.
/// </summary>
/// <remarks>
/// <see cref="RestartAsync"/> is what makes the crash scenarios meaningful: it disposes
/// every scope and service and builds a new container over the <b>same database file</b>, so
/// nothing survives except what was durably committed.
/// </remarks>
internal sealed class MutationHarness : IAsyncDisposable
{
	private ServiceProvider services;

	private MutationHarness(TestDatabase database, FakeMailProvider provider)
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

	public static async Task<MutationHarness> CreateAsync()
	{
		var harness = new MutationHarness(new TestDatabase(), new FakeMailProvider(ProviderShapes.Gmail));
		await harness.Database.MigrateAsync();
		await harness.SeedAsync();
		return harness;
	}

	private ServiceProvider Build() =>
		new ServiceCollection()
			.AddLogging()
			.AddPersistence(Database.Directory)
			.AddSingleton<TimeProvider>(Clock)
			.AddSingleton<IFaultInjector>(Faults)
			.AddSingleton<IMailProviderFactory>(new StubProviderFactory(Provider))
			.AddMutations()
			.BuildServiceProvider();

	/// <summary>Simulates a hard kill and restart: new process, same database file.</summary>
	public async Task RestartAsync()
	{
		await services.DisposeAsync();
		Faults.Clear();
		services = Build();
	}

	public AsyncServiceScope Scope() => services.CreateAsyncScope();

	public async Task<T> UsingAsync<T>(Func<IServiceProvider, Task<T>> work)
	{
		await using var scope = Scope();
		return await work(scope.ServiceProvider);
	}

	public async Task UsingAsync(Func<IServiceProvider, Task> work)
	{
		await using var scope = Scope();
		await work(scope.ServiceProvider);
	}

	public Guid AccountId => Account.Id;

	public Guid InboxId { get; private set; }

	public Guid ArchiveId { get; private set; }

	public Guid MessageId { get; private set; }

	private async Task SeedAsync()
	{
		await using var scope = Scope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();

		Account = new Account
		{
			Id = Guid.NewGuid(),
			DisplayName = "Fake",
			ProviderType = ProviderType.Gmail,
		};
		InboxId = Guid.NewGuid();
		ArchiveId = Guid.NewGuid();
		MessageId = Guid.NewGuid();

		// The fake server is seeded first, and the local occurrence id is whatever it minted
		// — never a value the test chose. A test that invents the provider's ids cannot
		// observe a move changing one.
		Provider.AddMailbox("INBOX", SpecialUse.Inbox);
		Provider.AddMailbox("ARCHIVE", SpecialUse.Archive);
		var occurrenceId = Provider.SeedMessage("INBOX", MessageId, DateTimeOffset.UnixEpoch);

		context.Accounts.Add(Account);
		context.Mailboxes.Add(NewMailbox(InboxId, "INBOX"));
		context.Mailboxes.Add(NewMailbox(ArchiveId, "ARCHIVE"));
		context.Messages.Add(
			new Message
			{
				Id = MessageId,
				AccountId = Account.Id,
				ProviderStableId = $"message-{MessageId}",
				ReceivedAt = DateTimeOffset.UnixEpoch,
				Occurrences =
				[
					new MessageMailbox
					{
						Id = Guid.NewGuid(),
						MailboxId = InboxId,
						ProviderOccurrenceId = occurrenceId,
					},
				],
			}
		);

		await context.SaveChangesAsync();
	}

	private Mailbox NewMailbox(Guid id, string name) =>
		new()
		{
			Id = id,
			AccountId = Account.Id,
			ProviderMailboxId = name,
			Name = name,
		};

	public async ValueTask DisposeAsync()
	{
		await services.DisposeAsync();
		await Database.DisposeAsync();
	}

	private sealed class StubProviderFactory(IMailProvider provider) : IMailProviderFactory
	{
		public IMailProvider For(ProviderType type) => provider;
	}
}

/// <summary>
/// Throws at one named point, once. A simulated kill must not be catchable by the error
/// handling under test, or the scenario proves nothing.
/// </summary>
internal sealed class ScriptedFaultInjector : IFaultInjector
{
	private string? armed;

	public List<string> Reachedbuffer { get; } = [];

	public void ArmAt(string point) => armed = point;

	public void Clear() => armed = null;

	public void Reached(string point)
	{
		Reachedbuffer.Add(point);
		if (armed != point)
		{
			return;
		}

		armed = null;
		throw new SimulatedCrashException(point);
	}
}
