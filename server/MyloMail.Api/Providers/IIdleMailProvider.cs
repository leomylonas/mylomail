using MyloMail.Api.Domain;

namespace MyloMail.Api.Providers;

/// <summary>
/// An optional long-lived provider notification session. Its completion is only a wakeup hint:
/// the caller must run the ordinary durable change stream afterwards, which alone owns cursor
/// advancement and observation persistence.
/// </summary>
public interface IIdleMailProvider
{
	/// <summary>Waits until the mailbox may have changed, the session is bounded, or cancellation is requested.</summary>
	Task WaitForMailboxChangeAsync(Account account, Mailbox mailbox, CancellationToken ct);
}
