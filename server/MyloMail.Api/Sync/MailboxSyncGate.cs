namespace MyloMail.Api.Sync;

/// <summary>
/// At most one change-stream sync per mailbox at a time. A request that arrives while one is under
/// way does not start another; it asks the running one to go round once more when it finishes, so a
/// change that landed after the running sync read the server is still picked up.
/// </summary>
/// <remarks>
/// Without this, pressing Sync twice, or a poll and an IDLE wake together, queued a second full
/// pass behind the first on the account's gate and repeated all its work.
/// </remarks>
public sealed class MailboxSyncGate
{
	private readonly Dictionary<Guid, bool> running = [];
	private readonly object guard = new();

	/// <summary>True when the caller now owns this mailbox's sync; false when one is running (and has been asked to repeat).</summary>
	public bool TryEnter(Guid mailboxId)
	{
		lock (guard)
		{
			if (running.ContainsKey(mailboxId))
			{
				running[mailboxId] = true;
				return false;
			}

			running[mailboxId] = false;
			return true;
		}
	}

	/// <summary>
	/// Called when a pass ends. True when another request arrived meanwhile and the owner should run again,
	/// still owning the mailbox; false when it is released.
	/// </summary>
	public bool ShouldRepeat(Guid mailboxId)
	{
		lock (guard)
		{
			if (running.TryGetValue(mailboxId, out var repeat) && repeat)
			{
				running[mailboxId] = false;
				return true;
			}

			running.Remove(mailboxId);
			return false;
		}
	}

	/// <summary>Releases the mailbox after a failed pass.</summary>
	public void Release(Guid mailboxId)
	{
		lock (guard)
		{
			running.Remove(mailboxId);
		}
	}
}
