using System.Collections.Concurrent;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Gives every account its own Hangfire queue and its own workers, so one account's backlog — a
/// rate-limited Gmail account with thousands of content fetches, an IMAP server that is slow to
/// answer — cannot occupy the workers another account's jobs need.
/// </summary>
/// <remarks>
/// A job belongs to an account's queue when its first parameter is named <c>accountId</c>, which
/// every account-scoped job in this codebase follows. Anything else (a contact operation or an
/// export, addressed by its own id) runs on the shared default queue.
/// </remarks>
public static class AccountQueues
{
	public static string? QueueFor(Job? job)
	{
		if (job is null)
		{
			return null;
		}

		var parameters = job.Method.GetParameters();
		if (
			parameters.Length == 0
			|| parameters[0].Name != "accountId"
			|| job.Args.Count == 0
			|| job.Args[0] is not Guid accountId
		)
		{
			return null;
		}

		return Name(accountId);
	}

	/// <summary>Hangfire queue names are lower-case letters, digits, underscores and dashes.</summary>
	public static string Name(Guid accountId) => $"account-{accountId:N}";
}

/// <summary>
/// Routes a job to its account's queue as it is enqueued — whether directly or when a scheduled
/// job falls due — and makes sure a server is serving that queue before the job can wait on it.
/// </summary>
public sealed class AccountQueueFilter(IServiceProvider services) : IElectStateFilter
{
	public void OnStateElection(ElectStateContext context)
	{
		if (context.CandidateState is not EnqueuedState enqueued)
		{
			return;
		}

		var queue = AccountQueues.QueueFor(context.BackgroundJob.Job);
		if (queue is null)
		{
			return;
		}

		enqueued.Queue = queue;
		// Absent in hosts that enqueue but never run jobs (tests), where nothing serves any queue.
		((AccountQueueServers?)services.GetService(typeof(AccountQueueServers)))?.Ensure(queue);
	}
}

/// <summary>
/// One small <see cref="BackgroundJobServer"/> per account queue, started the first time a job is
/// queued for that account and stopped with the application.
/// </summary>
public sealed class AccountQueueServers(JobStorage storage) : IDisposable
{
	/// <summary>
	/// Enough for sync, content download, a send and a mutation drain to progress together,
	/// without one account being able to take a share of the machine.
	/// </summary>
	private const int WorkersPerAccount = 4;

	private readonly ConcurrentDictionary<string, BackgroundJobServer> servers = new();
	private int stopped;

	public void Ensure(string queue)
	{
		if (Volatile.Read(ref stopped) != 0 || servers.ContainsKey(queue))
		{
			return;
		}

		servers.GetOrAdd(
			queue,
			static (name, storage) =>
				new BackgroundJobServer(
					new BackgroundJobServerOptions
					{
						Queues = [name],
						WorkerCount = WorkersPerAccount,
						ServerName = $"{Environment.MachineName}:{name}",
					},
					storage
				),
			storage
		);
	}

	public void Dispose()
	{
		Interlocked.Exchange(ref stopped, 1);
		foreach (var server in servers.Values)
		{
			server.SendStop();
		}

		foreach (var server in servers.Values)
		{
			server.Dispose();
		}

		servers.Clear();
	}
}
