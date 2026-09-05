using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Dispatches due outbox items and reconciles the ones whose outcome is unknown (§15).
/// </summary>
/// <inheritdoc cref="SyncJobs" path="/remarks"/>
/// <summary>Pushes an account's dirty drafts to the server.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class DraftJobs(DraftSyncService drafts)
{
	public Task PushAsync(Guid accountId, CancellationToken ct = default) =>
		drafts.PushAsync(accountId, ct);
}

/// <summary>Requests a draft push, so a save reaches the server without waiting for a restart.</summary>
public sealed class DraftDispatcher(IBackgroundJobClient jobs) : IDraftDispatcher
{
	public void RequestPush(Guid accountId) =>
		jobs.Enqueue<DraftJobs>(job => job.PushAsync(accountId, default));
}

/// <summary>Schedules an outbox run for when its next item is due.</summary>
public sealed class OutboxDispatcher(IBackgroundJobClient jobs) : IOutboxDispatcher
{
	public void RequestSend(Guid accountId, TimeSpan delay) =>
		jobs.Schedule<OutboxJobs>(
			job => job.RunAsync(accountId, default),
			delay > TimeSpan.Zero ? delay : TimeSpan.Zero
		);
}

[AutomaticRetry(Attempts = 0)]
public sealed class OutboxJobs(
	MyloMailDbContext context,
	OutboxService outbox,
	SendExecutor sender,
	SendReconciler reconciler,
	AccountGate gate,
	IBackgroundJobClient jobs,
	IHubEvents events,
	ILogger<OutboxJobs> logger
)
{
	/// <summary>
	/// Reconciles first, then sends what is due.
	/// </summary>
	/// <remarks>
	/// Reconciliation runs first on purpose: an unresolved send from a previous run must be
	/// settled before anything new is dispatched for the account, so that a user watching
	/// their outbox sees the ambiguous one resolve rather than sit behind fresh traffic.
	/// </remarks>
	public async Task RunAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null || !account.IsEnabled || account.AuthState == AuthState.NeedsReauth)
		{
			return;
		}

		if (gate.Delay(accountId) is var delay && delay > TimeSpan.Zero)
		{
			jobs.Schedule<OutboxJobs>(j => j.RunAsync(accountId, default), delay);
			return;
		}

		await reconciler.ReconcileAsync(accountId, ct);

		foreach (var item in await outbox.DueAsync(accountId, ct))
		{
			try
			{
				await sender.SendAsync(account, item.Id, ct);
			}
			catch (ProviderThrottledException ex)
			{
				// SendExecutor already set OutboxStatus.Scheduled and announced it before
				// rethrowing (§7) — the renderer already knows this item is back in the queue.
				gate.Throttle(accountId, ex.RetryAfter);
				jobs.Schedule<OutboxJobs>(j => j.RunAsync(accountId, default), ex.RetryAfter);
				return;
			}
			catch (ProviderAuthenticationException ex)
			{
				// Not the `account` loaded above: OutboxService's claim CAS runs a raw SQL
				// UPDATE and clears the whole context's change tracker afterward so its own
				// tracked copy can't go stale (see its own comment) — which detaches this one
				// too. Writing through it here would silently affect zero rows: it looks
				// attached (EF gives no error), but SaveChangesAsync has nothing queued for
				// it. A fresh, newly-tracked load is required.
				// SendExecutor already set OutboxStatus.Scheduled and announced it (§7) before
				// rethrowing — only the account-level signal remains this job's responsibility.
				var reloaded = await context.Accounts.FirstAsync(a => a.Id == accountId, ct);
				reloaded.AuthState = AuthState.NeedsReauth;
				reloaded.LastAuthError = ex.Message;
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, reloaded, ct);
				return;
			}
			catch (Credentials.CredentialStoreUnavailableException ex)
			{
				// Same detached-`account` hazard as the ProviderAuthenticationException branch
				// above — a fresh load is required. Not gated off like NeedsReauth: the next
				// scheduled run retrying on its own, once the OS store is reachable again, is
				// the recovery path (see SyncJobs.GuardAsync's matching comment). SendExecutor
				// already set OutboxStatus.Scheduled and announced it (§7) before rethrowing.
				var reloaded = await context.Accounts.FirstAsync(a => a.Id == accountId, ct);
				reloaded.AuthState = AuthState.CredentialStoreUnavailable;
				reloaded.LastAuthError = ex.Message;
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, reloaded, ct);
				return;
			}
			catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
			{
				// Same "already ambiguous" reasoning as below, but at Debug: a network-class
				// failure recurs every run while offline, and an Error per item per run is
				// exactly the per-job noise § Offline behaviour asks to be suppressed until
				// connectivity returns.
				logger.LogDebug(ex, "Send for outbox item {OutboxItemId} failed (offline).", item.Id);
			}
			catch (Exception ex)
			{
				// SendExecutor has already recorded the ambiguity. One send failing must not
				// stop the others: they are separate messages the user asked to send.
				logger.LogError(ex, "Send for outbox item {OutboxItemId} failed.", item.Id);
			}
		}

		// A full run with no credential-store failure means it's reachable again if it
		// wasn't before — self-clearing here avoids leaving a stale banner up once the real
		// problem is gone (mirrors SyncJobs.GuardAsync's success-path clear).
		var afterRun = await context.Accounts.FirstAsync(a => a.Id == accountId, ct);
		if (afterRun.AuthState == AuthState.CredentialStoreUnavailable)
		{
			afterRun.AuthState = AuthState.Connected;
			afterRun.LastAuthError = null;
			await context.SaveChangesAsync(ct);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, afterRun, ct);
		}
	}

	/// <summary>
	/// Schedules a queued send to fire when its undo window closes.
	/// </summary>
	/// <remarks>
	/// <b>Scheduled sends fire only while the app is running.</b> With in-memory job storage
	/// this Hangfire job exists for the current session only; what makes a pending send
	/// survive a restart at all is being re-enqueued from <c>OutboxItem</c> by startup
	/// reconciliation. A message scheduled for a time the app is closed will not send then.
	/// </remarks>
	public void ScheduleDispatch(Guid accountId, DateTimeOffset sendAt, TimeSpan fromNow)
	{
		logger.LogInformation("Outbox dispatch for account {AccountId} scheduled at {SendAt}.", accountId, sendAt);
		jobs.Schedule<OutboxJobs>(j => j.RunAsync(accountId, default), fromNow);
	}
}
