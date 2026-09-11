using Hangfire;
using MyloMail.Api.Contacts;

namespace MyloMail.Api.Scheduling;

/// <summary>Thin scheduler facade; durable ContactOperations, not Hangfire, own recovery.</summary>
[AutomaticRetry(Attempts = 0)]
public sealed class ContactJobs(
	ContactService contacts,
	ContactRefreshRegistry refreshes,
	AccountGate gate,
	IBackgroundJobClient jobs
)
{
	public Task ExecuteAsync(Guid operationId, CancellationToken ct) => contacts.ExecuteAsync(operationId, ct);
	public Task ReconcileAsync(Guid operationId, CancellationToken ct) => contacts.ReconcileAsync(operationId, ct);

	public Task StartRefreshAsync(Guid accountId)
	{
		if (refreshes.TryStart(accountId))
		{
			try
			{
				jobs.Enqueue<ContactJobs>(job => job.RefreshAsync(accountId, default));
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
		var repeat = true;
		var delay = TimeSpan.FromMinutes(5);
		try
		{
			repeat = await contacts.RefreshAsync(accountId, ct);
		}
		catch (Providers.ProviderThrottledException)
		{
			delay = gate.Delay(accountId);
		}
		finally
		{
			if (repeat)
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
			else refreshes.Stop(accountId);
		}
	}
}
