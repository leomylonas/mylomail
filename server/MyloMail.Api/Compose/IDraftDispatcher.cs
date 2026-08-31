namespace MyloMail.Api.Compose;

/// <summary>
/// Asks for an account's drafts to be pushed to the server.
/// </summary>
/// <remarks>
/// The same seam as the mutation and outbox dispatchers, for the same reason: the compose
/// path is covered by tests that must not need a job runner. Every one of these was added
/// after the same bug — work recorded durably and nothing driving it — so the pattern is
/// deliberate rather than incidental.
/// </remarks>
public interface IDraftDispatcher
{
	void RequestPush(Guid accountId);
}

/// <summary>Used where nothing executes jobs — tests, and hosts without a scheduler.</summary>
public sealed class NoDraftDispatcher : IDraftDispatcher
{
	public void RequestPush(Guid accountId) { }
}
