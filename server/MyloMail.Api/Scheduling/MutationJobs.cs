using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Mutations;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Drains an account's eligible mutation chains (§6).
/// </summary>
/// <inheritdoc cref="SyncJobs" path="/remarks"/>
/// <summary>Enqueues a drain when new intent arrives.</summary>
public sealed class MutationDispatcher(IBackgroundJobClient jobs) : IMutationDispatcher
{
	public void RequestDrain(Guid accountId) =>
		jobs.Enqueue<MutationJobs>(job => job.DrainAsync(accountId, default));
}

[AutomaticRetry(Attempts = 0)]
public sealed class MutationJobs(
	MyloMailDbContext context,
	MutationClaimService claims,
	MutationReconciler reconciler,
	MutationExecutor executor,
	AccountGate gate,
	IBackgroundJobClient jobs,
	IHubEvents events,
	ILogger<MutationJobs> logger
)
{
	private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

	/// <summary>
	/// Claims the eligible heads for one account and executes them in homogeneous batches.
	/// </summary>
	/// <remarks>
	/// Items are grouped by operation and payload because a batch call carries one operation
	/// with one set of arguments. Grouping does not widen the dispatch boundary — fifty items
	/// in one provider batch already share one — it only decides what can travel together.
	/// </remarks>
	public async Task DrainAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null || !account.IsEnabled || account.AuthState == AuthState.NeedsReauth)
		{
			return;
		}

		if (gate.Delay(accountId) > TimeSpan.Zero)
		{
			jobs.Schedule<MutationJobs>(j => j.DrainAsync(accountId, default), gate.Delay(accountId));
			return;
		}

		// A dispatched local attempt says only that the provider may have seen it. Settle
		// those attempts before claiming any work, so no move or deletion is blindly replayed.
		await reconciler.ReconcileAsync(accountId, ct);

		var claimed = await claims.ClaimAsync(accountId, Environment.MachineName, LeaseDuration, max: 50, ct);
		if (claimed.Count == 0)
		{
			return;
		}

		foreach (var batch in claimed.GroupBy(item => new
		{
			item.OperationKind,
			item.TargetMailboxId,
			item.DesiredIsRead,
			item.DesiredIsFlagged,
		}))
		{
			try
			{
				await executor.ExecuteAsync(account, [.. batch], ct);
			}
			catch (ProviderThrottledException ex)
			{
				gate.Throttle(accountId, ex.RetryAfter);
				jobs.Schedule<MutationJobs>(j => j.DrainAsync(accountId, default), ex.RetryAfter);
				return;
			}
			catch (ProviderAuthenticationException ex)
			{
				// Not the `account` loaded above: MutationClaimService's claim CAS runs a raw
				// SQL UPDATE and clears the whole context's change tracker afterward so its own
				// tracked copy can't go stale — which detaches this one too. Writing through it
				// here would silently affect zero rows: it looks attached (EF gives no error),
				// but SaveChangesAsync has nothing queued for it. A fresh, newly-tracked load
				// is required.
				var reloaded = await context.Accounts.FirstAsync(a => a.Id == accountId, ct);
				reloaded.AuthState = AuthState.NeedsReauth;
				reloaded.LastAuthError = ex.Message;
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, reloaded, ct);
				return;
			}
			catch (Exception ex)
			{
				// The attempt is already marked ambiguous by the executor. One batch failing
				// must not abandon the others: they are separate user intentions.
				logger.LogError(ex, "A mutation batch for account {AccountId} failed.", accountId);
			}
		}

		// More chains may have become eligible now that these heads are terminal.
		jobs.Enqueue<MutationJobs>(j => j.DrainAsync(accountId, default));
	}
}
