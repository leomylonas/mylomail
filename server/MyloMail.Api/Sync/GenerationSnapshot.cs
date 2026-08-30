using MyloMail.Api.Domain;

namespace MyloMail.Api.Sync;

/// <summary>
/// The topology generation each mailbox held when a piece of sync work was issued (§1).
/// </summary>
/// <remarks>
/// <para>
/// Sync pages, coverage jobs and content fetches carry the generation they were issued under
/// and are discarded if it no longer matches. Without that, an IMAP folder deleted and
/// recreated with the same name — or a Graph folder replaced — lets a page still in flight
/// resurrect or mutate state belonging to a mailbox that no longer exists.
/// </para>
/// <para>
/// The check covers <b>every</b> write a page can make, not only the upserts: a stale removal
/// deletes a real occurrence belonging to the new incarnation, and a stale flag change
/// rewrites its server-known state. Provider occurrence ids repeat across incarnations, so
/// neither failure announces itself.
/// </para>
/// </remarks>
public sealed class GenerationSnapshot
{
	private readonly IReadOnlyDictionary<string, int> generations;

	private GenerationSnapshot(IReadOnlyDictionary<string, int> generations)
	{
		this.generations = generations;
	}

	public IReadOnlyDictionary<string, int> Values => generations;

	public static GenerationSnapshot Capture(IEnumerable<Mailbox> mailboxes) =>
		new(
			mailboxes
				.Where(m => m.ProviderMailboxId is not null)
				.ToDictionary(m => m.ProviderMailboxId!, m => m.TopologyGeneration, StringComparer.Ordinal)
		);

	public static GenerationSnapshot From(IReadOnlyDictionary<string, int> values) =>
		new(new Dictionary<string, int>(values, StringComparer.Ordinal));

	/// <summary>
	/// Whether the mailbox is still the incarnation the work was issued against.
	/// </summary>
	/// <remarks>
	/// A mailbox absent from the snapshot passes: it was not part of the issued work, so there
	/// is no earlier incarnation to have diverged from. A mailbox present with a different
	/// generation fails.
	/// <para>
	/// There is deliberately no "skip the check" snapshot. An empty one would permit
	/// everything while reading at the call site as though it had been checked — which is
	/// exactly the shape of the hole this type was added to close, where a null generation
	/// silently disabled the guard on the replay path.
	/// </para>
	/// </remarks>
	public bool StillCurrent(string? providerMailboxId, Mailbox mailbox) =>
		providerMailboxId is null
		|| !generations.TryGetValue(providerMailboxId, out var issued)
		|| issued == mailbox.TopologyGeneration;
}
