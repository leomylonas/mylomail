using MyloMail.Api.Scheduling;
using Xunit;

namespace MyloMail.Api.Tests.Scheduling;

/// <summary>
/// <see cref="ConnectivityMonitor.IsNetworkFailure"/> gates whether a background job scheduler
/// reschedules quietly (a transient network blip) or stops the poll loop as a fatal error. An
/// <c>HttpClient</c> request timeout — CalDAV's own transport, and what the Gmail/Graph SDKs use
/// underneath — throws <see cref="OperationCanceledException"/> wrapping a
/// <see cref="TimeoutException"/>, a shape the exception-type list alone never covered.
/// </summary>
public sealed class ConnectivityMonitorTests
{
	[Fact]
	public void An_http_client_timeout_is_a_network_failure()
	{
		var timeout = new OperationCanceledException("The request timed out.", new TimeoutException());

		Assert.True(ConnectivityMonitor.IsNetworkFailure(timeout));
	}

	/// <summary>
	/// A genuine cancellation (a caller's own token tripping, e.g. account removal or shutdown)
	/// must NOT be treated as a network failure — that would silently reschedule work a caller
	/// deliberately asked to stop, instead of letting the cancellation propagate.
	/// </summary>
	[Fact]
	public void A_genuine_cancellation_is_not_a_network_failure()
	{
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		Exception cancellation;
		try
		{
			cts.Token.ThrowIfCancellationRequested();
			throw new InvalidOperationException("unreachable");
		}
		catch (OperationCanceledException ex)
		{
			cancellation = ex;
		}

		Assert.False(ConnectivityMonitor.IsNetworkFailure(cancellation));
	}
}
