using Hangfire;
using MyloMail.Api.Contacts;

namespace MyloMail.Api.Scheduling;

/// <summary>Thin scheduler facade; durable ContactOperations, not Hangfire, own recovery.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class ContactJobs(
	ContactService contacts,
	ContactRefreshRegistry refreshes,
	AccountGate gate,
	ConnectivityMonitor connectivity,
	IBackgroundJobClient jobs
)
{
	public async Task ExecuteAsync(Guid operationId, CancellationToken ct)
	{
		var workKey = $"{nameof(ExecuteAsync)}:{operationId}";
		if (!connectivity.CanRun(
				workKey,
				client => client.Enqueue<ContactJobs>(job => job.ExecuteAsync(operationId, default))
			))
		{
			return;
		}
		try
		{
			await contacts.ExecuteAsync(operationId, ct);
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			await connectivity.PauseAsync(
				workKey,
				client => client.Enqueue<ContactJobs>(job => job.ExecuteAsync(operationId, default))
			);
		}
	}

	public async Task ReconcileAsync(Guid operationId, CancellationToken ct)
	{
		var workKey = $"{nameof(ReconcileAsync)}:{operationId}";
		if (!connectivity.CanRun(
				workKey,
				client => client.Enqueue<ContactJobs>(job => job.ReconcileAsync(operationId, default))
			))
		{
			return;
		}
		try
		{
			await contacts.ReconcileAsync(operationId, ct);
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			await connectivity.PauseAsync(
				workKey,
				client => client.Enqueue<ContactJobs>(job => job.ReconcileAsync(operationId, default))
			);
		}
	}

	public Task StartRefreshAsync(Guid accountId)
	{
		if (refreshes.TryStart(accountId))
		{
			try
			{
				connectivity.DispatchOrDefer(
					$"{nameof(RefreshAsync)}:{accountId}",
					client => client.Enqueue<ContactJobs>(
						job => job.RefreshAsync(accountId, default)
					)
				);
			}
			catch
			{
				refreshes.Stop(accountId);
				throw;
			}
		}
		return Task.CompletedTask;
	}

	public async Task RefreshAsync(Guid accountId, CancellationToken ct)
	{
		var workKey = $"{nameof(RefreshAsync)}:{accountId}";
		if (!connectivity.CanRun(
				workKey,
				client => client.Enqueue<ContactJobs>(job => job.RefreshAsync(accountId, default))
			))
		{
			return;
		}

		var repeat = true;
		var delay = TimeSpan.FromMinutes(5);
		var paused = false;
		try
		{
			repeat = await contacts.RefreshAsync(accountId, ct);
		}
		catch (Providers.ProviderThrottledException)
		{
			delay = gate.Delay(accountId);
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			paused = true;
			await connectivity.PauseAsync(
				workKey,
				client => client.Enqueue<ContactJobs>(job => job.RefreshAsync(accountId, default))
			);
		}
		finally
		{
			if (!paused && repeat)
			{
				try
				{
					jobs.Schedule<ContactJobs>(
						job => job.RefreshAsync(accountId, default),
						delay
					);
				}
				catch
				{
					refreshes.Stop(accountId);
					throw;
				}
			}
			else if (!paused)
			{
				refreshes.Stop(accountId);
			}
		}
	}
}
