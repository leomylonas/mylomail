using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Transfers;

namespace MyloMail.Api.Scheduling;

/// <summary>Requests a run of an account's cross-account transfers.</summary>
public sealed class TransferDispatcher(ConnectivityMonitor connectivity) : ITransferDispatcher
{
	public void RequestRun(Guid targetAccountId, TimeSpan delay = default) =>
		connectivity.DispatchOrDefer(
			$"{nameof(TransferJobs)}:{targetAccountId}:{(delay > TimeSpan.Zero ? "later" : "now")}",
			client =>
			{
				if (delay > TimeSpan.Zero)
				{
					client.Schedule<TransferJobs>(job => job.RunAsync(targetAccountId, default), delay);
				}
				else
				{
					client.Enqueue<TransferJobs>(job => job.RunAsync(targetAccountId, default));
				}
			}
		);
}

/// <summary>
/// Carries out the mail being moved into an account from another (<see cref="CrossAccountTransferService"/>).
/// </summary>
/// <inheritdoc cref="SyncJobs" path="/remarks"/>
[AutomaticRetry(Attempts = 0)]
public sealed class TransferJobs(
	MyloMailDbContext context,
	CrossAccountTransferService transfers,
	AccountGate gate,
	ConnectivityMonitor connectivity,
	IBackgroundJobClient jobs,
	IHubEvents events,
	ILogger<TransferJobs> logger
)
{
	private static readonly TimeSpan StillWorkingDelay = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan CredentialStoreRetryDelay = TimeSpan.FromSeconds(30);

	/// <summary>
	/// The first parameter is the <i>target</i> account's id, which routes the job to that account's
	/// own queue and workers (<see cref="AccountQueues"/>): its provider is the one being written to.
	/// </summary>
	public async Task RunAsync(Guid accountId, CancellationToken ct = default)
	{
		var workKey = $"{nameof(TransferJobs)}:{accountId}";
		if (!connectivity.CanRun(workKey, client => client.Enqueue<TransferJobs>(job => job.RunAsync(accountId, default))))
		{
			return;
		}

		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null || !account.IsEnabled || account.AuthState is AuthState.NeedsReauth or AuthState.Error)
		{
			return;
		}

		if (gate.Delay(accountId) is var delay && delay > TimeSpan.Zero)
		{
			jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), delay);
			return;
		}

		try
		{
			var more = await transfers.RunAsync(account, ct);
			if (more)
			{
				// Something is waiting on the provider or on a look for the copy: come back to it.
				jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), StillWorkingDelay);
			}

			await WakeTargetAsync(accountId, ct);
		}
		catch (TransferSourceException ex)
		{
			// The account the mail is coming FROM refused. It is that account that is throttled or
			// signed out, not this one; the transfer stays waiting.
			switch (ex.InnerException)
			{
				case ProviderThrottledException throttled:
					gate.Throttle(ex.SourceAccountId, throttled.RetryAfter);
					jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), throttled.RetryAfter);
					break;
				case ProviderAuthenticationException authentication:
					// Picked up again when that account is reauthenticated (StartupScheduler.ResumeAccountAsync).
					await RecordAccountFailureAsync(ex.SourceAccountId, authentication.AccountState, authentication.Message, ct);
					break;
				case ProviderNotConfiguredException notConfigured:
					await RecordAccountFailureAsync(ex.SourceAccountId, AuthState.Error, notConfigured.Message, ct);
					break;
				default:
					await RecordAccountFailureAsync(ex.SourceAccountId, AuthState.CredentialStoreUnavailable, ex.Message, ct);
					jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), CredentialStoreRetryDelay);
					break;
			}
		}
		catch (ProviderThrottledException ex)
		{
			gate.Throttle(accountId, ex.RetryAfter);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct, gate);
			jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), ex.RetryAfter);
		}
		catch (ProviderAuthenticationException ex)
		{
			await RecordAccountFailureAsync(accountId, ex.AccountState, ex.Message, ct);
		}
		catch (ProviderNotConfiguredException ex)
		{
			await RecordAccountFailureAsync(accountId, AuthState.Error, ex.Message, ct);
		}
		catch (CredentialStoreUnavailableException ex)
		{
			await RecordAccountFailureAsync(accountId, AuthState.CredentialStoreUnavailable, ex.Message, ct);
			jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), CredentialStoreRetryDelay);
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			await connectivity.PauseAsync(workKey, client => client.Enqueue<TransferJobs>(job => job.RunAsync(accountId, default)));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			logger.LogError(ex, "Transfers into account {AccountId} failed; they are retried.", accountId);
			jobs.Schedule<TransferJobs>(job => job.RunAsync(accountId, default), StillWorkingDelay);
		}
	}

	/// <summary>Brings the copies just made into the target account's lists without waiting for its next poll.</summary>
	private async Task WakeTargetAsync(Guid accountId, CancellationToken ct)
	{
		var recently = DateTimeOffset.UtcNow.AddMinutes(-2);
		// Filtered here: SQLite does not compare DateTimeOffset values reliably.
		var mailboxIds = (
			await context
				.CrossAccountTransfers.Where(t =>
					t.TargetAccountId == accountId && t.State == CrossAccountTransferState.Completed && t.CompletedAt != null
				)
				.Select(t => new { t.TargetMailboxId, t.CompletedAt })
				.ToListAsync(ct)
		)
			.Where(t => t.CompletedAt > recently)
			.Select(t => t.TargetMailboxId)
			.Distinct()
			.ToList();
		foreach (var mailboxId in mailboxIds)
		{
			jobs.Enqueue<SyncJobs>(job => job.WakeChangeStreamAsync(accountId, mailboxId, default));
		}
	}

	private async Task RecordAccountFailureAsync(Guid accountId, AuthState state, string message, CancellationToken ct)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		account.AuthState = state;
		account.LastAuthError = message;
		await context.SaveChangesAsync(ct);
		await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
	}
}
