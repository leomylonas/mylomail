namespace MyloMail.Api.Scheduling;

/// <summary>Prevents topology runs from starting duplicate self-scheduling integrity loops.</summary>
public sealed class IntegrityRegistry
{
	private readonly HashSet<(Guid Account, Guid Mailbox)> running = [];
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
			running.RemoveWhere(scope => scope.Account == accountId);
		}
	}
}
