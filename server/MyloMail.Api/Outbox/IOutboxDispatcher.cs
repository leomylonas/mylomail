namespace MyloMail.Api.Outbox;

/// <summary>
/// Asks for an account's outbox to be run, when its next item is due.
/// </summary>
/// <remarks>
/// A seam rather than a Hangfire dependency, matching <c>IMutationDispatcher</c>: the outbox
/// is covered by fault-injection tests, and a job-client dependency in it would have to be
/// stubbed in every harness.
/// <para>
/// A request, never a guarantee. The item is durable before this is called, so a process that
/// dies before the job runs still has the send found by startup reconciliation.
/// </para>
/// </remarks>
public interface IOutboxDispatcher
{
	void RequestSend(Guid accountId, TimeSpan delay);
}

/// <summary>Used where nothing executes jobs — tests, and hosts without a scheduler.</summary>
public sealed class NoOutboxDispatcher : IOutboxDispatcher
{
	public void RequestSend(Guid accountId, TimeSpan delay) { }
}
