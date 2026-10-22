namespace MyloMail.Api.Transfers;

/// <summary>
/// Asks for an account's cross-account transfers to be run.
/// </summary>
/// <remarks>
/// A seam rather than a Hangfire dependency, matching <c>IMutationDispatcher</c> and
/// <c>IOutboxDispatcher</c>: the transfer core is covered by fault-injection tests, and a job-client
/// dependency in it would have to be stubbed in every harness. A request, never a guarantee — the
/// transfer is durable before this is called, and startup reconciliation finds it if the process
/// dies first.
/// </remarks>
public interface ITransferDispatcher
{
	/// <param name="targetAccountId">The account receiving the mail; transfers run on its queue.</param>
	void RequestRun(Guid targetAccountId, TimeSpan delay = default);
}

/// <summary>Used where nothing executes jobs — tests, and hosts without a scheduler.</summary>
public sealed class NoTransferDispatcher : ITransferDispatcher
{
	public void RequestRun(Guid targetAccountId, TimeSpan delay = default) { }
}
