using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
using MyloMail.Api.Scheduling;
using MyloMail.Api.Tests.Fakes;
using MyloMail.Api.Tests.Mutations;
using MyloMail.Api.Tests.Persistence;
using MyloMail.Api.Tests.Sync;
using MyloMail.Api.Transfers;

namespace MyloMail.Api.Tests.Transfers;

/// <summary>
/// Two accounts over one database, each with its own fake provider, and a host that can be killed
/// and restarted: what a move between accounts needs and no single-account harness has.
/// </summary>
internal sealed class TransferHarness : IAsyncDisposable
{
	private ServiceProvider services;
	private FakeMailbox sourceInbox = null!;

	private TransferHarness(TestDatabase database, ProviderCapabilities sourceShape, ProviderCapabilities targetShape)
	{
		Database = database;
		SourceProvider = new FakeMailProvider(sourceShape);
		TargetProvider = new FakeMailProvider(targetShape);
		Clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		Faults = new ScriptedFaultInjector();
		Events = new RecordingHubEvents();
		services = Build();
	}

	public TestDatabase Database { get; }
	public FakeMailProvider SourceProvider { get; }
	public FakeMailProvider TargetProvider { get; }
	public FakeMailbox TargetInbox { get; private set; } = null!;
	public FakeTimeProvider Clock { get; }
	public ScriptedFaultInjector Faults { get; }
	public RecordingHubEvents Events { get; }

	public Guid SourceAccountId { get; private set; }
	public Guid TargetAccountId { get; private set; }
	public Guid SourceInboxId { get; private set; }
	public Guid SourceTrashId { get; private set; }
	public Guid TargetInboxId { get; private set; }
	public Guid TargetDraftsId { get; private set; }

	public static async Task<TransferHarness> CreateAsync(
		ProviderCapabilities? source = null,
		ProviderCapabilities? target = null
	)
	{
		var harness = new TransferHarness(
			new TestDatabase(),
			source ?? ProviderShapes.Gmail,
			target ?? ProviderShapes.Imap(ImapCapabilityTier.QResync)
		);
		await harness.Database.MigrateAsync();
		await harness.SeedAsync();
		return harness;
	}

	private ServiceProvider Build() =>
		new ServiceCollection()
			.AddLogging()
			.AddPersistence(Database.Directory)
			.AddMutations()
			.AddSync()
			.AddScheduling()
			.AddScoped<MailHub>()
			.AddSingleton<TimeProvider>(Clock)
			.AddSingleton<IFaultInjector>(Faults)
			.AddSingleton<IMailProviderFactory>(new TwoAccountFactory(this))
			.AddSingleton<ICalendarProviderFactory>(new NoCalendars())
			.AddSingleton<ICredentialStore, InMemoryCredentialStore>()
			.AddSingleton<IHubEvents>(Events)
			.AddSingleton<IBackgroundJobClient>(new RecordingJobClient())
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

	/// <summary>Runs the target account's transfers once, as the job does.</summary>
	public Task<bool> RunAsync() =>
		UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			var account = await context.Accounts.SingleAsync(a => a.Id == TargetAccountId);
			return await scope.GetRequiredService<CrossAccountTransferService>().RunAsync(account);
		});

	public async Task<int> EnqueueAsync(params Guid[] messageIds) =>
		(await UsingAsync(scope =>
			scope
				.GetRequiredService<CrossAccountTransferService>()
				.EnqueueAsync(SourceAccountId, messageIds, TargetAccountId, TargetInboxId))).Count;

	/// <summary>A message in the source inbox, with its raw bytes stored, as a downloaded message is.</summary>
	public async Task<Guid> AddMessageAsync(string? messageId = "<one@example.test>", bool isRead = true, bool isFlagged = false, bool withRaw = true)
	{
		var id = Guid.NewGuid();
		// The source server holds the message too, as it does for any message in a synced mailbox.
		var occurrenceId = SourceProvider.SeedMessage("INBOX", id, DateTimeOffset.UnixEpoch);
		sourceInbox.Messages[occurrenceId].RawBytes = Raw(messageId);
		await UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Messages.Add(
				new Message
				{
					Id = id,
					AccountId = SourceAccountId,
					MessageIdHeader = messageId,
					Subject = "Hello",
					IsRead = isRead,
					IsFlagged = isFlagged,
					ReceivedAt = DateTimeOffset.UnixEpoch,
				}
			);
			context.MessageMailboxes.Add(
				new MessageMailbox { Id = Guid.NewGuid(), MessageId = id, MailboxId = SourceInboxId, ProviderOccurrenceId = occurrenceId }
			);
			if (withRaw)
			{
				context.MessageRaws.Add(new MessageRaw { MessageId = id, Content = Raw(messageId) });
			}

			await context.SaveChangesAsync();
		});
		return id;
	}

	public static byte[] Raw(string? messageId)
	{
		var header = messageId is null ? string.Empty : $"Message-ID: {messageId}\r\n";
		return System.Text.Encoding.ASCII.GetBytes(
			$"From: a@example.test\r\nTo: b@example.test\r\nSubject: Hello\r\nDate: Thu, 01 Jan 1970 00:00:00 +0000\r\n{header}\r\nBody\r\n"
		);
	}

	public Task<List<CrossAccountTransfer>> TransfersAsync() =>
		UsingAsync(async scope => await scope.GetRequiredService<MyloMailDbContext>().CrossAccountTransfers.AsNoTracking().ToListAsync());

	public Task<int> TrashMutationsAsync(Guid messageId) =>
		UsingAsync(async scope =>
			await scope
				.GetRequiredService<MyloMailDbContext>()
				.MutationItems.CountAsync(m =>
					m.AccountId == SourceAccountId && m.MessageId == messageId && m.OperationKind == MutationOperationKind.MoveToTrash));

	private async Task SeedAsync()
	{
		SourceAccountId = Guid.NewGuid();
		TargetAccountId = Guid.NewGuid();
		sourceInbox = SourceProvider.AddMailbox("INBOX", SpecialUse.Inbox);
		SourceProvider.AddMailbox("TRASH", SpecialUse.Trash);
		TargetInbox = TargetProvider.AddMailbox("INBOX", SpecialUse.Inbox);
		TargetProvider.AddMailbox("Drafts", SpecialUse.Drafts);
		await UsingAsync(async scope =>
		{
			var context = scope.GetRequiredService<MyloMailDbContext>();
			context.Accounts.Add(new Account { Id = SourceAccountId, DisplayName = "Source", ProviderType = SourceProvider.Capabilities.Type, InitialSyncMode = InitialSyncMode.Full });
			context.Accounts.Add(new Account { Id = TargetAccountId, DisplayName = "Target", ProviderType = TargetProvider.Capabilities.Type, InitialSyncMode = InitialSyncMode.Full });
			SourceInboxId = Guid.NewGuid();
			SourceTrashId = Guid.NewGuid();
			TargetInboxId = Guid.NewGuid();
			TargetDraftsId = Guid.NewGuid();
			context.Mailboxes.AddRange(
				new Mailbox { Id = SourceInboxId, AccountId = SourceAccountId, Name = "Inbox", ProviderMailboxId = "INBOX", SpecialUse = SpecialUse.Inbox },
				new Mailbox { Id = SourceTrashId, AccountId = SourceAccountId, Name = "Trash", ProviderMailboxId = "TRASH", SpecialUse = SpecialUse.Trash },
				new Mailbox { Id = TargetInboxId, AccountId = TargetAccountId, Name = "Inbox", ProviderMailboxId = "INBOX", SpecialUse = SpecialUse.Inbox },
				new Mailbox { Id = TargetDraftsId, AccountId = TargetAccountId, Name = "Drafts", ProviderMailboxId = "Drafts", SpecialUse = SpecialUse.Drafts }
			);
			await context.SaveChangesAsync();
		});
	}

	public async ValueTask DisposeAsync()
	{
		await services.DisposeAsync();
		await Database.DisposeAsync();
	}

	private sealed class TwoAccountFactory(TransferHarness harness) : IMailProviderFactory
	{
		public IMailProvider For(Account account) =>
			account.Id == harness.SourceAccountId ? harness.SourceProvider : harness.TargetProvider;
	}

	private sealed class NoCalendars : ICalendarProviderFactory
	{
		public ICalendarProvider For(Account account) => throw new NotSupportedException();
	}
}
