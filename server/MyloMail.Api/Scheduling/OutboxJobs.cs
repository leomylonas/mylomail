using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Outbox;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Dispatches due outbox items and reconciles the ones whose outcome is unknown (§15).
/// </summary>
/// <inheritdoc cref="SyncJobs" path="/remarks"/>
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
				gate.Throttle(accountId, ex.RetryAfter);
				jobs.Schedule<OutboxJobs>(j => j.RunAsync(accountId, default), ex.RetryAfter);
				return;
			}
			catch (ProviderAuthenticationException ex)
			{
				account.AuthState = AuthState.NeedsReauth;
				account.LastAuthError = ex.Message;
				await context.SaveChangesAsync(ct);
				return;
			}
			catch (Exception ex)
			{
				// SendExecutor has already recorded the ambiguity. One send failing must not
				// stop the others: they are separate messages the user asked to send.
				logger.LogError(ex, "Send for outbox item {OutboxItemId} failed.", item.Id);
			}
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
