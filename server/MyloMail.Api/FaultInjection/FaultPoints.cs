namespace MyloMail.Api.FaultInjection;

/// <summary>
/// Named points at which a test may kill the process (§16). Naming them in production code
/// is deliberate: a kill point is only meaningful if it sits exactly at the durable boundary
/// it names, and a test-side approximation of "roughly here" tests nothing.
/// </summary>
/// <remarks>
/// The default injector does nothing and is not on any hot path beyond a null check.
/// </remarks>
public static class FaultPoints
{
	/// <summary>After the optimistic local commit, before the mutation is enqueued.</summary>
	public const string AfterOptimisticCommit = "mutation.after-optimistic-commit";

	/// <summary>After the item is enqueued, before its attempt is marked dispatched.</summary>
	public const string AfterEnqueueBeforeDispatched = "mutation.after-enqueue-before-dispatched";

	/// <summary>
	/// After the durable <c>Dispatched</c> write, before the provider call. The window this
	/// names is the reason the write exists.
	/// </summary>
	public const string AfterDispatchedBeforeProviderCall = "mutation.after-dispatched-before-provider-call";

	/// <summary>After the provider call returns, before its results are persisted.</summary>
	public const string AfterProviderCallBeforeResults = "mutation.after-provider-call-before-results";

	/// <summary>
	/// Mid-page: the page has been read from the provider, and neither it nor the cursor that
	/// covers it has been committed.
	/// </summary>
	public const string SyncPageBeforeCommit = "sync.page-before-commit";

	/// <summary>
	/// After a page and its cursor have been committed, before the next page is requested.
	/// The boundary a resumed walk restarts from.
	/// </summary>
	public const string SyncPageAfterCommit = "sync.page-after-commit";

	/// <summary>
	/// Gmail only: staged history has been drained durably and coverage has completed, but the
	/// staged events have not yet been replayed into the canonical model.
	/// </summary>
	public const string SyncBeforeStagedReplay = "sync.before-staged-replay";

	/// <summary>
	/// A draft push's provider call has returned (the remote draft now genuinely exists),
	/// before that result is saved. The window a batched save would have crossed without
	/// noticing — the remote side effect already happened and cannot be replayed away.
	/// </summary>
	public const string DraftPushAfterProviderCallBeforeCommit = "draft-push.after-provider-call-before-commit";
}

/// <summary>Kills the process at a named point, or does nothing.</summary>
public interface IFaultInjector
{
	/// <summary>Called at a named durable boundary. Throws to simulate a hard kill.</summary>
	void Reached(string point);
}

/// <summary>The production implementation: every point is a no-op.</summary>
public sealed class NullFaultInjector : IFaultInjector
{
	public static readonly NullFaultInjector Instance = new();

	public void Reached(string point) { }
}

/// <summary>
/// Thrown by a test injector to simulate the process dying. It is deliberately not derived
/// from any exception the mutation code catches: a simulated kill must not be swallowed by
/// the error handling under test, or the scenario silently proves nothing.
/// </summary>
public sealed class SimulatedCrashException(string point)
	: Exception($"Simulated crash at fault point '{point}'.")
{
	public string Point { get; } = point;
}
