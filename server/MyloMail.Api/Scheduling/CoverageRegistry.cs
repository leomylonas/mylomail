namespace MyloMail.Api.Scheduling;

/// <summary>
/// Owns each coverage walk's self-scheduling job for the lifetime of this process.
/// </summary>
/// <remarks>
/// <para>
/// Coverage state is durable but Hangfire storage is not. The registry prevents topology,
/// settings, and startup from creating parallel walkers for the same resume token; it is
/// intentionally cleared by process restart, when startup reconstructs ownership from SQLite.
/// </para>
/// <para>
/// IMAP and Graph have one walk per mailbox, so each is owned under its own mailbox id. Gmail
/// has one walk per account, owned under <see cref="AccountWalkScope"/> — a sentinel, not a
/// mailbox, so that exactly one job exists per account however many labels it has or how
/// they come and go.
/// </para>
/// </remarks>
public sealed class CoverageRegistry
{
	/// <summary>
	/// The owner key of an account-scoped coverage walk. Distinct from every mailbox id and from
	/// <see cref="SyncJobs.TopologyScope"/>, which the poll registry uses the same way.
	/// </summary>
	internal static readonly Guid AccountWalkScope = new("22222222-2222-2222-2222-222222222222");

	private readonly HashSet<(Guid AccountId, Guid MailboxId)> running = [];
	private readonly object guard = new();

	public bool TryStart(Guid accountId, Guid mailboxId)
	{
		lock (guard)
		{
			return running.Add((accountId, mailboxId));
		}
	}

	public void Stop(Guid accountId, Guid mailboxId)
	{
		lock (guard)
		{
			running.Remove((accountId, mailboxId));
		}
	}

	public void StopAll(Guid accountId)
	{
		lock (guard)
		{
			running.RemoveWhere(scope => scope.AccountId == accountId);
		}
	}
}
