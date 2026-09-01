using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;
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
		CalendarProvider = new FakeCalendarProvider();
		Faults = new ScriptedFaultInjector();
		Clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
		services = Build();
	}

	public TestDatabase Database { get; }

	public FakeMailProvider Provider { get; }

	public FakeCalendarProvider CalendarProvider { get; }

	public ScriptedFaultInjector Faults { get; }

	public FakeTimeProvider Clock { get; }

	/// <summary>Records the §7 events raised, so a test can assert which one fired.</summary>
	public RecordingHubEvents Events { get; } = new();

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
			.AddSingleton<ICalendarProviderFactory>(new StubCalendarFactory(CalendarProvider))
			.AddSingleton<IHubEvents>(Events)
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
		public IMailProvider For(Account account) => provider;
	}

	private sealed class StubCalendarFactory(ICalendarProvider provider) : ICalendarProviderFactory
	{
		public ICalendarProvider For(Account account) => provider;
	}
}

internal sealed class FakeCalendarProvider : ICalendarProvider
{
	public List<string?> Cursors { get; } = [];

	public bool InvalidateFirstBaselineContinuation { get; set; }

	private bool baselineContinuationReturned;

	public ProviderType Type => ProviderType.Imap;

	public Task<IReadOnlyList<CalendarDto>> ListCalendarsAsync(Account account, CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<CalendarDto>>([new("calendar", "Calendar", null, true)]);

	public Task<CalendarSyncResult> SyncCalendarAsync(Account account, Calendar calendar, string? cursor, string? continuation, CancellationToken ct)
	{
		Cursors.Add(cursor);
		if (InvalidateFirstBaselineContinuation && continuation is not null)
		{
			InvalidateFirstBaselineContinuation = false;
			throw new ProviderCursorInvalidException("The baseline continuation expired.");
		}
		if (InvalidateFirstBaselineContinuation && !baselineContinuationReturned)
		{
			baselineContinuationReturned = true;
			return Task.FromResult(new CalendarSyncResult(null, "baseline-next", [Event("partial")], []));
		}
		return Task.FromResult(
			cursor is null
				? new CalendarSyncResult("token-1", null, [Event("one")], [])
				: new CalendarSyncResult("token-2", null, [], [])
		);
	}

	private static CalendarEventDto Event(string id) => new()
	{
		ProviderEventId = id,
		ICalUid = id,
		ProviderRevision = "etag-1",
		Start = DateTimeOffset.UnixEpoch,
		End = DateTimeOffset.UnixEpoch.AddHours(1),
	};

	public Task<string> CreateEventAsync(Account account, Calendar calendar, CalendarEventDto ev, CancellationToken ct) => throw new NotSupportedException();
	public Task UpdateEventAsync(Account account, CalendarEvent ev, string? expectedETag, CancellationToken ct) => throw new NotSupportedException();
	public Task DeleteEventAsync(Account account, CalendarEvent ev, CancellationToken ct) => throw new NotSupportedException();
	public Task RespondToInviteAsync(Account account, CalendarEvent ev, InviteResponse response, string? comment, CancellationToken ct) => throw new NotSupportedException();
}

/// <summary>Records what was announced rather than announcing it.</summary>
internal sealed class RecordingHubEvents : IHubEvents
{
	public List<MessageSummaryDto> Received { get; } = [];

	public List<MessageSummaryDto> Updated { get; } = [];

	public List<Guid> Deleted { get; } = [];

	public List<MailboxSummaryDto> Mailboxes { get; } = [];

	public List<Guid> TreeChanges { get; } = [];

	public List<Guid> Drafts { get; } = [];

	public List<Guid> CalendarEvents { get; } = [];

	public List<Guid> CalendarConflicts { get; } = [];

	public List<NotificationDto> Notifications { get; } = [];

	public void Clear()
	{
		Received.Clear();
		Updated.Clear();
		Deleted.Clear();
		Mailboxes.Clear();
		TreeChanges.Clear();
		Drafts.Clear();
		CalendarEvents.Clear();
		CalendarConflicts.Clear();
		Notifications.Clear();
	}

	public Task MessageReceivedAsync(MessageSummaryDto message)
	{
		Received.Add(message);
		return Task.CompletedTask;
	}

	public Task MessageUpdatedAsync(MessageSummaryDto message)
	{
		Updated.Add(message);
		return Task.CompletedTask;
	}

	public Task DraftUpdatedAsync(Guid draftId)
	{
		Drafts.Add(draftId);
		return Task.CompletedTask;
	}

	public Task CalendarEventUpdatedAsync(Guid eventId)
	{
		CalendarEvents.Add(eventId);
		return Task.CompletedTask;
	}

	public Task CalendarConflictDetectedAsync(Guid eventId)
	{
		CalendarConflicts.Add(eventId);
		return Task.CompletedTask;
	}

	public Task MessageDeletedAsync(Guid messageId)
	{
		Deleted.Add(messageId);
		return Task.CompletedTask;
	}

	public Task MailboxUpdatedAsync(MailboxSummaryDto mailbox)
	{
		Mailboxes.Add(mailbox);
		return Task.CompletedTask;
	}

	public Task MailboxTreeChangedAsync(Guid accountId)
	{
		TreeChanges.Add(accountId);
		return Task.CompletedTask;
	}

	public Task SyncProgressAsync(SyncProgressDto progress) => Task.CompletedTask;

	public Task OutboxStatusChangedAsync(OutboxItemDto item) => Task.CompletedTask;

	public Task MessageSyncFailedAsync(MutationFailureDto failure) => Task.CompletedTask;

	public Task AccountStatusChangedAsync(AccountDto account) => Task.CompletedTask;

	public Task NotificationReadyAsync(NotificationDto notification)
	{
		Notifications.Add(notification);
		return Task.CompletedTask;
	}
}
