namespace MyloMail.Api.Scheduling;

/// <summary>In-memory active-mailbox selection for bounded IMAP IDLE sessions.</summary>
/// <remarks>The selection is UI convenience, never durable work: restart falls back to Inbox and polling covers every folder.</remarks>
public sealed class ImapIdleRegistry
{
	private readonly object guard = new();
	private readonly Dictionary<string, (Guid AccountId, Guid MailboxId)> activeMailboxes = [];

	public void SetActiveMailbox(string connectionId, Guid accountId, Guid mailboxId)
	{
		lock (guard)
		{
			activeMailboxes[connectionId] = (accountId, mailboxId);
		}
	}

	public void RemoveConnection(string connectionId)
	{
		lock (guard)
		{
			activeMailboxes.Remove(connectionId);
		}
	}

	public IReadOnlyCollection<(Guid AccountId, Guid MailboxId)> Snapshot()
	{
		lock (guard)
		{
			return activeMailboxes.Values.Distinct().ToArray();
		}
	}
}

/// <summary>Coalesces IDLE hints to one queued/in-flight wake plus one dirty rerun.</summary>
public sealed class ImapIdleWakeRegistry
{
	private readonly object guard = new();
	private readonly Dictionary<(Guid AccountId, Guid MailboxId), bool> dirty = [];

	/// <returns>True only when the caller must enqueue the first wake.</returns>
	public bool Request((Guid AccountId, Guid MailboxId) scope)
	{
		lock (guard)
		{
			if (dirty.ContainsKey(scope))
			{
				dirty[scope] = true;
				return false;
			}
			dirty.Add(scope, false);
			return true;
		}
	}

	/// <returns>True when one coalesced rerun must be enqueued.</returns>
	public bool Complete((Guid AccountId, Guid MailboxId) scope)
	{
		lock (guard)
		{
			if (!dirty.TryGetValue(scope, out var rerun)) return false;
			if (rerun)
			{
				dirty[scope] = false;
				return true;
			}
			dirty.Remove(scope);
			return false;
		}
	}

	/// <summary>Clears a reservation when its Hangfire enqueue did not succeed.</summary>
	public void Release((Guid AccountId, Guid MailboxId) scope)
	{
		lock (guard)
		{
			dirty.Remove(scope);
		}
	}
}
