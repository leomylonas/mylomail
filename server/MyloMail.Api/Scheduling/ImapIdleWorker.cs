using System.Collections.Concurrent;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Holds bounded IMAP IDLE sessions for Inbox and each account's actively viewed mailbox.
/// </summary>
/// <remarks>
/// Sessions are non-authoritative wakeup hints. On completion this worker only enqueues the
/// existing change-stream job; it never reads observations or advances a cursor. A lost IDLE
/// connection is therefore indistinguishable from a missed hint and polling remains correct.
/// </remarks>
public sealed class ImapIdleWorker(
	IServiceScopeFactory scopes,
	ImapIdleRegistry activeMailboxes,
	ImapIdleWakeRegistry wakeups,
	ConnectivityMonitor connectivity,
	TimeProvider clock,
	IBackgroundJobClient jobs,
	ILogger<ImapIdleWorker> logger
) : BackgroundService
{
	private readonly ConcurrentDictionary<(Guid AccountId, Guid MailboxId), IdleSession> sessions = [];
	private readonly ConcurrentDictionary<(Guid AccountId, Guid MailboxId), DateTimeOffset> retryAfter = [];

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					var wanted = connectivity.IsOnline
						? await WantedScopesAsync(stoppingToken)
						: [];
					var retired = new List<Task>();
					foreach (var (scope, session) in sessions)
					{
						if (wanted.Contains(scope)) continue;
						if (!sessions.TryRemove(scope, out _)) continue;
						session.Cancel();
						retired.Add(session.Completion);
					}
					await Task.WhenAll(retired);
					foreach (var scope in wanted)
					{
						if (retryAfter.TryGetValue(scope, out var retryAt)
							&& retryAt > clock.GetUtcNow()) continue;
						retryAfter.TryRemove(scope, out _);
						Start(scope, stoppingToken);
					}
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex, "Could not refresh IMAP IDLE sessions.");
				}
				await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
			}
		}
		catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
		{
		}
		finally
		{
			var remaining = sessions.Values.ToArray();
			foreach (var session in remaining) session.Cancel();
			try
			{
				await Task.WhenAll(remaining.Select(session => session.Completion));
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Could not cleanly stop all IMAP IDLE sessions.");
			}
		}
	}

	private void Start(
		(Guid AccountId, Guid MailboxId) scope,
		CancellationToken stoppingToken
	)
	{
		var candidate = new IdleSession(
			CancellationTokenSource.CreateLinkedTokenSource(stoppingToken)
		);
		if (!sessions.TryAdd(scope, candidate))
		{
			candidate.Dispose();
			return;
		}
		candidate.Completion = RunSessionAsync(scope, candidate);
	}

	private async Task RunSessionAsync((Guid AccountId, Guid MailboxId) scope, IdleSession session)
	{
		try
		{
			while (!session.Cancellation.IsCancellationRequested)
			{
				await using var serviceScope = scopes.CreateAsyncScope();
				var context = serviceScope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
				var account = await context.Accounts.FirstOrDefaultAsync(
					a => a.Id == scope.AccountId
						&& a.IsEnabled
						&& a.PollingEnabled
						&& a.ProviderType == ProviderType.Imap
						&& a.AuthState == AuthState.Connected,
					session.Cancellation.Token
				);
				var mailbox = await context.Mailboxes.FirstOrDefaultAsync(
					m => m.Id == scope.MailboxId && m.AccountId == scope.AccountId,
					session.Cancellation.Token
				);
				if (account is null || mailbox is null) return;

				var provider = serviceScope.ServiceProvider.GetRequiredService<IMailProviderFactory>().For(account);
				if (provider is not IIdleMailProvider idle) return;
				await idle.WaitForMailboxChangeAsync(account, mailbox, session.Cancellation.Token);
				if (!session.Cancellation.IsCancellationRequested)
				{
					retryAfter.TryRemove(scope, out _);
					if (wakeups.Request(scope))
					{
						try
						{
							jobs.Enqueue<SyncJobs>(
								job => job.WakeChangeStreamAsync(
									scope.AccountId,
									scope.MailboxId,
									default
								)
							);
						}
						catch
						{
							wakeups.Release(scope);
							throw;
						}
					}
				}
			}
		}
		catch (OperationCanceledException) when (session.Cancellation.IsCancellationRequested)
		{
		}
		catch (CredentialStoreUnavailableException ex)
		{
			await PauseForCredentialStoreAsync(scope.AccountId, ex.Message);
		}
		catch (ProviderAuthenticationException ex)
		{
			await PauseForAuthenticationAsync(scope.AccountId, ex.Message);
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			retryAfter[scope] = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
			logger.LogDebug(
				ex,
				"IMAP IDLE session is offline for account {AccountId}, mailbox {MailboxId}.",
				scope.AccountId,
				scope.MailboxId
			);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "IMAP IDLE session failed for account {AccountId}, mailbox {MailboxId}.", scope.AccountId, scope.MailboxId);
		}
		finally
		{
			((ICollection<KeyValuePair<(Guid AccountId, Guid MailboxId), IdleSession>>)sessions)
				.Remove(new(scope, session));
			session.Dispose();
		}
	}

	private async Task PauseForAuthenticationAsync(Guid accountId, string message)
	{
		await using var scope = scopes.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var account = await context.Accounts.SingleOrDefaultAsync(
			candidate => candidate.Id == accountId,
			CancellationToken.None
		);
		if (account is null || account.AuthState == AuthState.NeedsReauth) return;
		account.AuthState = AuthState.NeedsReauth;
		account.LastAuthError = message;
		await context.SaveChangesAsync(CancellationToken.None);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(
			context,
			scope.ServiceProvider.GetRequiredService<IHubEvents>(),
			account,
			CancellationToken.None
		);
	}

	private async Task PauseForCredentialStoreAsync(Guid accountId, string message)
	{
		await using var scope = scopes.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var account = await context.Accounts.SingleOrDefaultAsync(
			candidate => candidate.Id == accountId,
			CancellationToken.None
		);
		if (account is null || account.AuthState == AuthState.CredentialStoreUnavailable) return;
		account.AuthState = AuthState.CredentialStoreUnavailable;
		account.LastAuthError = message;
		await context.SaveChangesAsync(CancellationToken.None);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(
			context,
			scope.ServiceProvider.GetRequiredService<IHubEvents>(),
			account,
			CancellationToken.None
		);
	}

	internal async Task<HashSet<(Guid AccountId, Guid MailboxId)>> WantedScopesAsync(CancellationToken ct)
	{
		await using var scope = scopes.CreateAsyncScope();
		var context = scope.ServiceProvider.GetRequiredService<MyloMailDbContext>();
		var accountIds = await context.Accounts
			.Where(a => a.IsEnabled
				&& a.PollingEnabled
				&& a.ProviderType == ProviderType.Imap
				&& a.AuthState == AuthState.Connected)
			.Select(a => a.Id)
			.ToListAsync(ct);
		var inboxes = await context.Mailboxes
			.Where(m => (m.SpecialUseOverride ?? m.SpecialUse) == SpecialUse.Inbox
				&& accountIds.Contains(m.AccountId))
			.OrderBy(m => m.Id)
			.Select(m => new { m.AccountId, m.Id })
			.ToListAsync(ct);
		var wanted = inboxes
			.GroupBy(mailbox => mailbox.AccountId)
			.Select(group => group.First())
			.Select(mailbox => (mailbox.AccountId, mailbox.Id))
			.ToHashSet();
		var active = activeMailboxes.Snapshot()
			.Where(selection => accountIds.Contains(selection.AccountId))
			.ToHashSet();
		var activeMailboxIds = active.Select(selection => selection.MailboxId).ToList();
		var activeRows = (await context.Mailboxes
			.Where(mailbox => activeMailboxIds.Contains(mailbox.Id)
				&& accountIds.Contains(mailbox.AccountId))
			.Select(mailbox => new { mailbox.AccountId, mailbox.Id })
			.ToListAsync(ct))
			.Where(mailbox => active.Contains((mailbox.AccountId, mailbox.Id)))
			.ToList();
		foreach (var activeMailbox in activeRows)
			wanted.Add((activeMailbox.AccountId, activeMailbox.Id));
		return wanted;
	}
	private sealed class IdleSession(CancellationTokenSource cancellation) : IDisposable
	{
		private bool disposed;
		public CancellationTokenSource Cancellation { get; } = cancellation;
		public Task Completion { get; set; } = Task.CompletedTask;

		public void Cancel()
		{
			lock (this)
			{
				if (!disposed) Cancellation.Cancel();
			}
		}

		public void Dispose()
		{
			lock (this)
			{
				if (disposed) return;
				disposed = true;
				Cancellation.Dispose();
			}
		}
	}
}
