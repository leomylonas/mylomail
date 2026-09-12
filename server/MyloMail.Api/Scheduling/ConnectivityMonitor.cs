using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Hangfire;
using MailKit.Security;
using MyloMail.Api.Hubs;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Tracks one app-wide online/offline signal (§15) and broadcasts <c>ConnectivityChanged</c>
/// when it flips. Network-class failures retain one deduplicated continuation per work scope,
/// then enqueue those continuations only after a probe confirms recovery. The UI therefore
/// shows one calm offline state instead of per-mailbox errors multiplying every poll.
/// </summary>
/// <remarks>
/// <para>
/// Discovered two ways, per §3: the OS's own network-availability event reacts immediately to
/// an interface going up or down, while a low-frequency poll (<see cref="ProbeAsync"/>, wired
/// to Hangfire's minute-granular recurring scheduler) catches missed events and gives retained
/// jobs another bounded opportunity to test their actual provider path.
/// </para>
/// <para>
/// The deferred dictionary is not a job store. It contains only enqueue delegates for work
/// whose intent/state is already durable in SQLite; <see cref="StartupScheduler"/> reconstructs
/// that work after a process restart, just as it does for in-memory Hangfire storage.
/// </para>
/// <para>
/// Deliberately not wired into individual provider call sites. Every provider is constructed
/// per-account by its factory from connection settings, not resolved from the DI container, so
/// threading this singleton into that constructor path would touch provider code three times
/// over for a coarse, app-wide signal that the OS event and the probe already deliver.
/// </para>
/// </remarks>
public sealed class ConnectivityMonitor : IDisposable
{
	private readonly IHubEvents events;
	private readonly ILogger<ConnectivityMonitor> logger;
	private readonly IBackgroundJobClient jobs;
	private readonly object guard = new();
	private readonly Dictionary<string, Action<IBackgroundJobClient>> deferred = [];
	private volatile bool online = true;

	public ConnectivityMonitor(
		IHubEvents events,
		IBackgroundJobClient jobs,
		ILogger<ConnectivityMonitor> logger
	)
	{
		this.events = events;
		this.jobs = jobs;
		this.logger = logger;
		NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
	}

	public bool IsOnline => online;

	/// <summary>
	/// Returns whether a job may touch its provider now. Offline jobs register one deduplicated
	/// continuation which is enqueued immediately after connectivity is confirmed again.
	/// </summary>
	public bool CanRun(string key, Action<IBackgroundJobClient> resume)
	{
		lock (guard)
		{
			if (online)
			{
				return true;
			}
			deferred[key] = resume;
			return false;
		}
	}

	/// <summary>Dispatches new durable work now, or records its one recovery enqueue while offline.</summary>
	public void DispatchOrDefer(string key, Action<IBackgroundJobClient> dispatch)
	{
		lock (guard)
		{
			if (!online)
			{
				deferred[key] = dispatch;
				return;
			}
		}
		Dispatch(key, dispatch);
	}

	/// <summary>
	/// Marks a newly observed network failure offline and retains the failed job for recovery.
	/// </summary>
	public async Task PauseAsync(string key, Action<IBackgroundJobClient> resume)
	{
		bool changed;
		lock (guard)
		{
			changed = online;
			online = false;
			deferred[key] = resume;
		}
		if (changed)
		{
			await BroadcastAsync(false);
		}
	}

	/// <summary>Marks a network failure observed by work with its own recovery loop.</summary>
	public Task MarkOfflineAsync() => ReportAsync(false);

	/// <summary>
	/// Whether an exception represents the socket/DNS/TLS-handshake layer being unreachable,
	/// not a provider rejecting the request — the distinction the background job classes use
	/// to pause until connectivity recovery instead of surfacing every offline poll attempt as
	/// a fresh failure, without threading this monitor into provider construction itself (see
	/// the class remarks above for why that stays out of scope).
	/// </summary>
	/// <remarks>
	/// Deliberately exception-shape-based, not gated on <see cref="IsOnline"/>: the probe only
	/// samples once a minute, so a job hitting a real network failure right after the last
	/// "online" sample must still be treated as network-class immediately, not misfiled as a
	/// genuine application bug until the next probe tick catches up.
	/// </remarks>
	public static bool IsNetworkFailure(Exception ex) =>
		ex is IOException or SocketException or HttpRequestException or SslHandshakeException
		// HttpClient (CalDAV's own transport, and the Gmail/Graph SDKs underneath) reports its
		// own request timeout as OperationCanceledException wrapping a TimeoutException, not any
		// of the shapes above — indistinguishable by type alone from a caller's genuine
		// cancellation, whose InnerException is never a TimeoutException. Left unclassified, a
		// slow/unresponsive server would fall through to every job scheduler's fatal-error catch
		// (which stops the poll loop rather than rescheduling), the same as a permanent failure.
		|| ex is OperationCanceledException { InnerException: TimeoutException };

	private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
	{
		if (!e.IsAvailable)
		{
			// The interface itself is gone — no need to wait for the next probe tick to say so.
			_ = ReportAsync(false).ContinueWith(
				t => logger.LogWarning(t.Exception, "Failed to broadcast connectivity loss."),
				TaskContinuationOptions.OnlyOnFaulted
			);
			return;
		}

		// Interface availability is enough to permit one bounded provider trial. It is not
		// proof of internet reachability (a captive portal also reports available), but the
		// provider call is the only probe that cannot be blocked independently of the user's
		// configured mail service. A failed trial marks the process offline again.
		_ = ReportAsync(true);
	}

	/// <summary>The low-frequency recovery probe (§3), run every minute by Hangfire.</summary>
	public async Task ProbeAsync(CancellationToken ct = default)
	{
		ct.ThrowIfCancellationRequested();
		await ReportAsync(NetworkInterface.GetIsNetworkAvailable());
	}

	internal async Task ReportAsync(bool reachable)
	{
		bool changed;
		KeyValuePair<string, Action<IBackgroundJobClient>>[] resumes = [];
		lock (guard)
		{
			changed = online != reachable;
			online = reachable;
			if (reachable && deferred.Count > 0)
			{
				resumes = [.. deferred];
				deferred.Clear();
			}
		}

		if (changed)
		{
			await BroadcastAsync(reachable);
		}
		if (reachable)
		{
			foreach (var resume in resumes)
			{
				try
				{
					resume.Value(jobs);
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Could not resume {ConnectivityWorkKey}.", resume.Key);
					lock (guard)
					{
						deferred[resume.Key] = resume.Value;
					}
				}
			}
		}
	}

	private void Dispatch(string key, Action<IBackgroundJobClient> dispatch)
	{
		try
		{
			dispatch(jobs);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Could not enqueue {ConnectivityWorkKey}.", key);
			throw;
		}
	}

	private async Task BroadcastAsync(bool reachable)
	{
		try
		{
			await events.ConnectivityChangedAsync(reachable);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not broadcast connectivity state {ConnectivityState}.", reachable);
		}
	}


	public void Dispose() => NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
}
