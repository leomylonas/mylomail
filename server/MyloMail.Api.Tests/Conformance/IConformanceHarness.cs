using MyloMail.Api.Domain;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Tests.Conformance;

/// <summary>
/// Supplies one provider, already authenticated and seeded, to the shared conformance
/// suite. Every provider — and every IMAP capability tier — implements this once, and
/// thereby inherits the whole suite.
/// </summary>
/// <remarks>
/// The harness is the only provider-specific code in conformance testing. The assertions
/// themselves never branch on <see cref="IMailProvider.Type"/>: a suite that special-cased
/// providers would stop being evidence that the abstraction holds, which is the entire
/// purpose of running it (§16, stage B).
/// </remarks>
public interface IConformanceHarness : IAsyncDisposable
{
	IMailProvider Provider { get; }

	Account Account { get; }

	/// <summary>A mailbox messages can be seeded into and moved out of.</summary>
	Mailbox Source { get; }

	/// <summary>A distinct mailbox messages can be moved into.</summary>
	Mailbox Destination { get; }

	/// <summary>Places a message in <paramref name="mailbox"/> and returns a reference to that occurrence.</summary>
	Task<MessageOccurrenceRef> SeedMessageAsync(Mailbox mailbox, CancellationToken ct = default);

	/// <summary>A valid cursor for <paramref name="mailbox"/>, as of now.</summary>
	Task<ProviderCursorState> BaselineCursorAsync(Mailbox mailbox, CancellationToken ct = default);

	/// <summary>
	/// Seeds enough messages that a single sync page cannot hold them all, so cursor
	/// advancement can be observed mid-walk.
	/// </summary>
	Task SeedPageOverflowAsync(Mailbox mailbox, CancellationToken ct = default);

	/// <summary>
	/// Produces a cursor the server will reject: an IMAP <c>UIDVALIDITY</c> change, an
	/// expired Gmail <c>historyId</c>, a Graph delta link that has gone <c>410</c>.
	/// </summary>
	Task<ProviderCursorState> ExpiredCursorAsync(Mailbox mailbox, CancellationToken ct = default);

	/// <summary>
	/// A reference the server will reject — a deleted or never-existent occurrence. Used to
	/// prove batch results are genuinely per item rather than one aggregate outcome.
	/// </summary>
	Task<MessageOccurrenceRef> UnresolvableOccurrenceAsync(Mailbox mailbox, CancellationToken ct = default);
}
