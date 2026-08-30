namespace MyloMail.Api.Scheduling;

/// <summary>
/// Tracks which self-scheduling poll loops are already running.
/// </summary>
/// <remarks>
/// <para>
/// A self-scheduling job enqueues its own successor, so anything that starts one a second
/// time doubles the polling rate for that scope — and topology reconciliation runs
/// repeatedly. Without this, every reconciliation would add another loop, and the account
/// would poll faster and faster until it was throttled permanently.
/// </para>
/// <para>
/// In-memory, like the throttle gate: job storage is in-memory too, so after a restart no
/// loop is running and none should be believed to be.
/// </para>
/// </remarks>
public sealed class PollRegistry
{
	private readonly HashSet<(Guid Account, Guid Mailbox)> running = [];
	private readonly object guard = new();

	/// <summary>True if this scope's loop was not already running, and is now claimed.</summary>
	public bool TryStart(Guid accountId, Guid mailboxId)
	{
		lock (guard)
		{
			return running.Add((accountId, mailboxId));
		}
	}

	/// <summary>Releases the scope so it can be started again — after a pause, or a restart.</summary>
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
			running.RemoveWhere(scope => scope.Account == accountId);
		}
	}
}
