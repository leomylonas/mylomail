using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MailKit.Security;
using MyloMail.Api.Hubs;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Tracks one app-wide online/offline signal (§15) and broadcasts <c>ConnectivityChanged</c>
/// when it flips — network-class failures suppress per-job retry noise until connectivity
/// returns, and the UI shows one calm offline state instead of per-mailbox errors multiplying
/// every poll.
/// </summary>
/// <remarks>
/// <para>
/// Discovered two ways, per §3: the OS's own network-availability event reacts immediately to
/// an interface going up or down, and a low-frequency probe (<see cref="ProbeAsync"/>, wired to
/// Hangfire's minute-granular recurring scheduler — this signal has no need for anything
/// finer) catches what the OS event cannot, such as a captive portal or an interface that stays
/// "up" while the actual path to the internet is gone.
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
	private readonly object guard = new();
	private volatile bool online = true;

	public ConnectivityMonitor(IHubEvents events, ILogger<ConnectivityMonitor> logger)
	{
		this.events = events;
		this.logger = logger;
		NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
	}

	public bool IsOnline => online;

	/// <summary>
	/// Whether an exception represents the socket/DNS/TLS-handshake layer being unreachable,
	/// not a provider rejecting the request — the distinction the background job classes use
	/// to reschedule quietly instead of surfacing every offline poll attempt as a fresh failure
	/// (§ Offline behaviour), without threading this monitor into provider construction itself
	/// (see the class remarks above for why that stays out of scope).
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
			void FireAndLog() => Report(false).ContinueWith(
				t => logger.LogWarning(t.Exception, "Failed to broadcast connectivity loss."),
				TaskContinuationOptions.OnlyOnFaulted
			);
			FireAndLog();
			return;
		}

		// An interface reappearing is not proof of a working path to the internet (a captive
		// portal reports "available" too), so this asks the probe rather than assuming online.
		_ = ProbeAsync(default);
	}

	/// <summary>The low-frequency probe (§3), run every minute by Hangfire's recurring scheduler.</summary>
	public async Task ProbeAsync(CancellationToken ct = default)
	{
		var reachable = await CanReachInternetAsync(ct);
		await Report(reachable);
	}

	private async Task Report(bool reachable)
	{
		bool changed;
		lock (guard)
		{
			changed = online != reachable;
			online = reachable;
		}

		if (changed)
		{
			await events.ConnectivityChangedAsync(reachable);
		}
	}

	private static async Task<bool> CanReachInternetAsync(CancellationToken ct)
	{
		try
		{
			using var client = new TcpClient();
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
			await client.ConnectAsync("1.1.1.1", 443, linked.Token);
			return client.Connected;
		}
		catch
		{
			return false;
		}
	}

	public void Dispose() => NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
}
