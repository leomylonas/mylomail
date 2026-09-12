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

	/// <summary>A mailbox health failure is applied, before its transaction commits.</summary>
	public const string MailboxHealthAfterApplyBeforeCommit = "mailbox-health.after-apply-before-commit";

	/// <summary>After a sync page is applied locally, before its data and cursor commit.</summary>
	public const string SyncPageAfterApplyBeforeCommit = "sync.page-after-apply-before-commit";

	/// <summary>
	/// After a page and its cursor have been committed, before the next page is requested.
	/// The boundary a resumed walk restarts from.
	/// </summary>
	public const string SyncPageAfterCommit = "sync.page-after-commit";

	/// <summary>After a reconciled mailbox tree is written, before its transaction commits.</summary>
	public const string TopologyAfterApplyBeforeCommit =
		"topology.after-apply-before-commit";

	/// <summary>
	/// After an account coverage policy and every inherited mailbox reset are applied, before
	/// that single transaction commits.
	/// </summary>
	public const string AccountCoveragePolicyAfterApplyBeforeCommit =
		"account-coverage-policy.after-apply-before-commit";

	/// <summary>
	/// Gmail only: staged history has been drained durably and coverage has completed, but the
	/// staged events have not yet been replayed into the canonical model.
	/// </summary>
	public const string SyncBeforeStagedReplay = "sync.before-staged-replay";

	/// <summary>After fetched content is applied locally, before its transaction commits.</summary>
	public const string ContentAfterApplyBeforeCommit = "content.after-apply-before-commit";

	/// <summary>After response headers and part of an attachment body reach the client.</summary>
	public const string AttachmentDownloadMidTransfer =
		"attachment-download.mid-transfer";

	/// <summary>After cursor invalidation state is changed, before its atomic save.</summary>
	public const string CursorInvalidationAfterApplyBeforeCommit =
		"cursor-invalidation.after-apply-before-commit";

	/// <summary>
	/// A draft push's provider call has returned (the remote draft now genuinely exists),
	/// before that result is saved. The window a batched save would have crossed without
	/// noticing — the remote side effect already happened and cannot be replayed away.
	/// </summary>
	public const string DraftPushAfterProviderCallBeforeCommit = "draft-push.after-provider-call-before-commit";

	/// <summary>
	/// A calendar provider accepted an initial event creation, but the returned server identity
	/// has not yet committed locally. Recovery searches by the durably stored iCalendar UID.
	/// </summary>
	public const string CalendarCreateAfterProviderCallBeforeCommit =
		"calendar-create.after-provider-call-before-commit";

	/// <summary>
	/// A calendar update and any shared-resource revision propagation are applied locally,
	/// after the provider accepted the write but before the atomic local commit.
	/// </summary>
	public const string CalendarUpdateAfterProviderCallBeforeCommit =
		"calendar-update.after-provider-call-before-commit";

	/// <summary>After a contact operation is durably dispatched, before its provider call.</summary>
	public const string ContactAfterDispatchedBeforeProviderCall =
		"contact.after-dispatched-before-provider-call";

	/// <summary>After a contact provider call returns, before its result is persisted.</summary>
	public const string ContactAfterProviderCallBeforeResults =
		"contact.after-provider-call-before-results";

	/// <summary>After contact observations and their cursor are staged, before either commits.</summary>
	public const string ContactRefreshBeforeCommit = "contact-refresh.before-commit";
	/// <summary>After fallback threads are rebuilt, before their one-time completion marker.</summary>
	public const string MessageThreadBackfillBeforeCompletion =
		"message-thread-backfill.before-completion";
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
