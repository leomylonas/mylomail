using MyloMail.Api.Compose;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// The stored form of a staged change page.
/// </summary>
/// <remarks>
/// The cursor is deliberately not stored with the page. It was already committed with the
/// staging row, and a replayed page must not be able to move a cursor a second time.
/// </remarks>
internal static class SyncPagePayload
{
	public static string Serialize(
		SyncResult result,
		IReadOnlyList<RemoteDraftPayload> remoteDrafts,
		GenerationSnapshot generations
	) =>
		SqliteJson.Serialize(
			new StagedPage(result.Upserted, result.FlagChanges, result.Removed, remoteDrafts, generations.Values)
		);

	public static (SyncResult Result, IReadOnlyList<RemoteDraftPayload> RemoteDrafts, GenerationSnapshot Generations) Deserialize(string payload)
	{
		var page =
			SqliteJson.Deserialize<StagedPage>(payload)
			?? throw new InvalidOperationException("A staged change page could not be read back.");

		return (
			new SyncResult(null, null, page.Upserted, page.FlagChanges, page.Removed),
			page.RemoteDrafts,
			GenerationSnapshot.From(page.Generations)
		);
	}

	/// <summary>
	/// The page as observed, with the topology generations it was observed under. Both are
	/// needed: replay happens after coverage, potentially hours later, by which time a mailbox
	/// may have been deleted and recreated.
	/// </summary>
	private sealed record StagedPage(
		IReadOnlyList<MessageDto> Upserted,
		IReadOnlyList<OccurrenceFlagChange> FlagChanges,
		IReadOnlyList<OccurrenceRemoval> Removed,
		IReadOnlyList<RemoteDraftPayload> RemoteDrafts,
		IReadOnlyDictionary<string, int> Generations
	);
}
