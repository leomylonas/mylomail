namespace MyloMail.Api.Mutations;

/// <summary>
/// Asks for an account's eligible mutations to be executed soon.
/// </summary>
/// <remarks>
/// <para>
/// A seam rather than a direct Hangfire dependency, for the same reason as <c>IHubEvents</c>:
/// the mutation core is what fault-injection tests exercise, and a job-client dependency in it
/// would have to be stubbed in every harness.
/// </para>
/// <para>
/// This is a request, not a guarantee, and never a substitute for durability. The intent is
/// already committed before this is called; if the process dies before the job runs, startup
/// reconciliation finds the item anyway.
/// </para>
/// </remarks>
public interface IMutationDispatcher
{
	void RequestDrain(Guid accountId);
}

/// <summary>Used where nothing executes jobs — tests, and hosts without a scheduler.</summary>
public sealed class NoMutationDispatcher : IMutationDispatcher
{
	public void RequestDrain(Guid accountId) { }
}
