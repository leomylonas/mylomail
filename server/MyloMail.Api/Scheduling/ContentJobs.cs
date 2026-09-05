using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Fetches message content in the background (§1).
/// </summary>
/// <remarks>
/// <para>
/// Runs at lower priority than metadata sync, and one message at a time: an account should
/// become usable — a sidebar, a message list — before it becomes searchable, and a content
/// sweep that saturated the provider would delay exactly that.
/// </para>
/// <para>
/// One message per job, enqueuing its own successor, so a large backlog survives a restart at
/// message granularity rather than being lost or restarted wholesale (§6).
/// </para>
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class ContentJobs(
	MyloMailDbContext context,
	ContentAcquisition acquisition,
	AccountGate gate,
	IBackgroundJobClient jobs,
	IHubEvents events,
	ILogger<ContentJobs> logger
)
{
	private const int BatchLimit = 25;

	public async Task FetchNextAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null || !account.IsEnabled || account.AuthState == AuthState.NeedsReauth)
		{
			return;
		}

		if (gate.Delay(accountId) is var delay && delay > TimeSpan.Zero)
		{
			jobs.Schedule<ContentJobs>(job => job.FetchNextAsync(accountId, default), delay);
			return;
		}

		var pending = await PendingAsync(accountId, ct);
		if (pending is null)
		{
			return;
		}

		try
		{
			await acquisition.AcquireAsync(account, pending.Value, ct);
		}
		catch (ProviderThrottledException ex)
		{
			gate.Throttle(accountId, ex.RetryAfter);
			jobs.Schedule<ContentJobs>(job => job.FetchNextAsync(accountId, default), ex.RetryAfter);
			return;
		}
		catch (CredentialStoreUnavailableException ex)
		{
			// No claim CAS runs ahead of this fetch (unlike MutationJobs/OutboxJobs), so
			// `account` is still the tracked, attached instance — no reload needed.
			account.AuthState = AuthState.CredentialStoreUnavailable;
			account.LastAuthError = ex.Message;
			await context.SaveChangesAsync(ct);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);

			logger.LogWarning("Account {AccountId}'s credential store could not be reached.", accountId);
			return;
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			// Same "acquisition already recorded it" reasoning as below, but at Debug: a
			// network-class failure recurs every fetch while offline, and a Warning per message
			// per fetch is exactly the per-job noise § Offline behaviour asks to be suppressed
			// until connectivity returns. (The attempt-budget consequence of a network failure
			// against ContentAcquisition's retry cap is a separate, pre-existing concern —
			// tracked in docs/handover.md's Next-task list rather than folded into this pass.)
			logger.LogDebug(ex, "Skipping content for message {MessageId} (offline).", pending.Value);
		}
		catch (Exception ex)
		{
			// The message is marked Failed by the acquisition itself. One unreadable message
			// must not stop the queue behind it.
			logger.LogWarning(ex, "Skipping content for message {MessageId}.", pending.Value);
		}

		if (account.AuthState == AuthState.CredentialStoreUnavailable)
		{
			// Not gated off from retrying (see the catch above) — a fetch reaching this far
			// without hitting that exception again means the store is reachable now.
			account.AuthState = AuthState.Connected;
			account.LastAuthError = null;
			await context.SaveChangesAsync(ct);
			await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
		}

		jobs.Enqueue<ContentJobs>(job => job.FetchNextAsync(accountId, default));
	}

	/// <summary>
	/// The next message needing content.
	/// </summary>
	/// <remarks>
	/// <c>Fetching</c> is included because a crash leaves messages in it, and nothing else
	/// would ever pick them up — the content path is idempotent, so re-fetching one is
	/// cheaper than leaving it permanently blank.
	/// </remarks>
	private async Task<Guid?> PendingAsync(Guid accountId, CancellationToken ct)
	{
		var candidates = await context
			.Messages.Where(m => m.AccountId == accountId)
			.Join(
				context.MessageContentStates.Where(c =>
					c.Status == ContentStatus.Queued || c.Status == ContentStatus.Fetching
				),
				m => m.Id,
				c => c.MessageId,
				(m, _) => m
			)
			.Take(BatchLimit)
			.AsNoTracking()
			.ToListAsync(ct);

		// Newest first: the messages a user is most likely to open are the ones they can see.
		return candidates.OrderByDescending(m => m.ReceivedAt).FirstOrDefault()?.Id;
	}
}
