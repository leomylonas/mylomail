namespace MyloMail.Api.Scheduling;

/// <summary>
/// Owns each mailbox's self-scheduling coverage walk for the lifetime of this process.
/// </summary>
/// <remarks>
/// Coverage state is durable but Hangfire storage is not. The registry prevents topology,
/// settings, and startup from creating parallel walkers for the same resume token; it is
/// intentionally cleared by process restart, when startup reconstructs ownership from SQLite.
/// </remarks>
public sealed class CoverageRegistry
{
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
