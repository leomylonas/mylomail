using System.Text.Json;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;

namespace MyloMail.Api.Providers.Graph;

public sealed partial class GraphMailProvider
{
	private const string TopologyCursorPrefix = "mylomail-graph-topology-v1.";
	private const string RootTopologyStream = "$root";
	private static readonly string[] TopologySelect =
	[
		"id",
		"displayName",
		"parentFolderId",
		"totalItemCount",
		"unreadItemCount",
	];

	public async Task<MailboxTopologyResult> SyncMailboxTopologyAsync(
		Account account,
		string? cursor,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var specialUses = await SpecialUsesAsync(client, ct);

		try
		{
			return await ReadMailboxTopologyAsync(client, cursor, specialUses, ct);
		}
		catch (Microsoft.Kiota.Abstractions.ApiException ex)
			when (cursor is not null && ex.ResponseStatusCode is 400 or 410)
		{
			throw new ProviderCursorInvalidException(
				"Graph mailbox topology cursor has been invalidated.",
				ex
			);
		}
	}

	internal static async Task<MailboxTopologyResult> ReadMailboxTopologyAsync(
		GraphServiceClient client,
		string? cursor,
		IReadOnlyDictionary<string, SpecialUse> specialUses,
		CancellationToken ct
	)
	{
		var isFullSnapshot = cursor is null;
		var streams = isFullSnapshot
			? new Dictionary<string, GraphTopologyStreamCursor>(StringComparer.Ordinal)
			: new Dictionary<string, GraphTopologyStreamCursor>(
				ParseTopologyCursor(cursor!).Streams,
				StringComparer.Ordinal
			);
		var upserted = new Dictionary<string, MailboxDto>(StringComparer.Ordinal);
		var removed = new HashSet<string>(StringComparer.Ordinal);
		var removalCandidates = new HashSet<string>(StringComparer.Ordinal);

		if (isFullSnapshot)
		{
			await BaselineStreamsAsync(
				client,
				streams,
				upserted,
				removed,
				removalCandidates,
				specialUses,
				[(RootTopologyStream, null)],
				ct
			);
		}
		else
		{
			foreach (var (streamId, _) in OrderStreamsByDepth(streams))
			{
				if (streamId != RootTopologyStream && HasRemovedAncestor(streamId, streams, removed))
				{
					continue;
				}

				var stream = streams[streamId];
				var page = await ReadStreamAsync(client, streamId, stream.DeltaLink, ct);
				streams[streamId] = stream with
				{
					DeltaLink = page.DeltaLink
						?? throw new InvalidOperationException("Completed Graph topology page lacks a cursor."),
				};
				var changes = CollectStream(page.Folders, specialUses);
				MergeStreamChanges(changes, upserted, removed, removalCandidates);

				foreach (var folder in changes.Upserted.Values)
				{
					if (streams.TryGetValue(folder.ProviderMailboxId, out var existing))
					{
						streams[folder.ProviderMailboxId] = existing with
						{
							ParentProviderMailboxId = folder.ParentProviderMailboxId,
						};
					}
				}
			}


			var newStreams = upserted.Values
				.Where(folder => !streams.ContainsKey(folder.ProviderMailboxId))
				.Select(folder => (folder.ProviderMailboxId, folder.ParentProviderMailboxId))
				.ToList();
			await BaselineStreamsAsync(
				client,
				streams,
				upserted,
				removed,
				removalCandidates,
				specialUses,
				newStreams,
				ct
			);
			await ResolveRemovalsAsync(
				client,
				streams,
				upserted,
				removed,
				removalCandidates,
				specialUses,
				ct
			);
		}

		removed.ExceptWith(upserted.Keys);
		PruneRemovedStreams(streams, removed);

		return new MailboxTopologyResult(
			[.. upserted.Values],
			[.. removed],
			SerializeTopologyCursor(new GraphTopologyCursor(streams)),
			isFullSnapshot
		);
	}

	private static async Task BaselineStreamsAsync(
		GraphServiceClient client,
		Dictionary<string, GraphTopologyStreamCursor> streams,
		Dictionary<string, MailboxDto> upserted,
		HashSet<string> removed,
		HashSet<string> removalCandidates,
		IReadOnlyDictionary<string, SpecialUse> specialUses,
		IEnumerable<(string StreamId, string? ParentProviderMailboxId)> initial,
		CancellationToken ct
	)
	{
		var pending = new Queue<(string StreamId, string? ParentProviderMailboxId)>(initial);
		var visited = new HashSet<string>(StringComparer.Ordinal);

		while (pending.TryDequeue(out var item))
		{
			if (!visited.Add(item.StreamId) || removed.Contains(item.StreamId))
			{
				continue;
			}
			if (streams.TryGetValue(item.StreamId, out var existing))
			{
				// A newly discovered parent can baseline a child whose own stream already
				// exists. That is a move, not permission to replace the child's advanced
				// cursor with a fresh baseline that could skip descendant removals.
				streams[item.StreamId] = existing with
				{
					ParentProviderMailboxId = item.ParentProviderMailboxId,
				};
				continue;
			}

			var page = await ReadStreamAsync(client, item.StreamId, null, ct);
			streams[item.StreamId] = new GraphTopologyStreamCursor(
				page.DeltaLink
					?? throw new InvalidOperationException("Completed Graph topology page lacks a cursor."),
				item.ParentProviderMailboxId
			);
			var changes = CollectStream(page.Folders, specialUses);
			MergeStreamChanges(changes, upserted, removed, removalCandidates);

			foreach (var folder in changes.Upserted.Values)
			{
				pending.Enqueue((folder.ProviderMailboxId, folder.ParentProviderMailboxId));
			}
		}
	}

	private static async Task<GraphTopologyPage> ReadStreamAsync(
		GraphServiceClient client,
		string streamId,
		string? cursor,
		CancellationToken ct
	)
	{
		var folders = new List<MailFolder>();
		var url = cursor;
		string? deltaLink = null;

		do
		{
			GraphTopologyPage page;
			if (streamId == RootTopologyStream)
			{
				var response = url is null
					? await ThrottleAwareAsync(
						() => client.Me.MailFolders.Delta.GetAsDeltaGetResponseAsync(
							configuration => configuration.QueryParameters.Select = TopologySelect,
							ct
						)
					)
					: await ThrottleAwareAsync(
						() => client.Me.MailFolders.Delta.WithUrl(url).GetAsDeltaGetResponseAsync(null, ct)
					);
				page = new GraphTopologyPage(
					response?.Value ?? [],
					response?.OdataNextLink,
					response?.OdataDeltaLink
				);
			}
			else
			{
				var delta = client.Me.MailFolders[streamId].ChildFolders.Delta;
				var response = url is null
					? await ThrottleAwareAsync(
						() => delta.GetAsDeltaGetResponseAsync(
							configuration => configuration.QueryParameters.Select = TopologySelect,
							ct
						)
					)
					: await ThrottleAwareAsync(
						() => delta.WithUrl(url).GetAsDeltaGetResponseAsync(null, ct)
					);
				page = new GraphTopologyPage(
					response?.Value ?? [],
					response?.OdataNextLink,
					response?.OdataDeltaLink
				);
			}

			folders.AddRange(page.Folders);
			url = page.NextLink;
			deltaLink = page.DeltaLink ?? deltaLink;
		} while (url is not null);

		return new GraphTopologyPage(
			folders,
			null,
			deltaLink
				?? throw new InvalidOperationException("Graph topology delta walk ended without a delta link.")
		);
	}

	private static GraphTopologyChanges CollectStream(
		IEnumerable<MailFolder> folders,
		IReadOnlyDictionary<string, SpecialUse> specialUses
	)
	{
		var upserted = new Dictionary<string, MailboxDto>(StringComparer.Ordinal);
		var removed = new HashSet<string>(StringComparer.Ordinal);
		foreach (var folder in folders.Where(folder => folder.Id is not null))
		{
			if (!IsUpsert(folder))
			{
				upserted.Remove(folder.Id!);
				removed.Add(folder.Id!);
				continue;
			}

			removed.Remove(folder.Id!);
			upserted[folder.Id!] = ToMailboxDto(folder, specialUses);
		}
		return new GraphTopologyChanges(upserted, removed);
	}

	private static void MergeStreamChanges(
		GraphTopologyChanges changes,
		Dictionary<string, MailboxDto> upserted,
		HashSet<string> removed,
		HashSet<string> removalCandidates
	)
	{
		removalCandidates.UnionWith(changes.Removed);
		foreach (var providerMailboxId in changes.Removed)
		{
			if (!upserted.ContainsKey(providerMailboxId))
			{
				removed.Add(providerMailboxId);
			}
		}
		foreach (var (providerMailboxId, mailbox) in changes.Upserted)
		{
			upserted[providerMailboxId] = mailbox;
			removed.Remove(providerMailboxId);
		}
	}

	private static async Task ResolveRemovalsAsync(
		GraphServiceClient client,
		Dictionary<string, GraphTopologyStreamCursor> streams,
		Dictionary<string, MailboxDto> upserted,
		HashSet<string> removed,
		IReadOnlySet<string> removalCandidates,
		IReadOnlyDictionary<string, SpecialUse> specialUses,
		CancellationToken ct
	)
	{
		var pending = new Stack<string>(
			removalCandidates.OrderByDescending(providerMailboxId =>
				StreamDepth(providerMailboxId, streams)
			)
		);
		var visited = new HashSet<string>(StringComparer.Ordinal);
		while (pending.TryPop(out var providerMailboxId))
		{
			if (!visited.Add(providerMailboxId))
			{
				continue;
			}
			try
			{
				var folder = await ThrottleAwareAsync(
					() => client.Me.MailFolders[providerMailboxId].GetAsync(
						configuration => configuration.QueryParameters.Select = TopologySelect,
						ct
					)
				) ?? throw new InvalidOperationException(
					"Graph returned an empty folder response while resolving a topology removal."
				);
				if (!IsUpsert(folder))
				{
					throw new InvalidOperationException(
						"Graph returned an incomplete folder while resolving a topology removal."
					);
				}

				var mailbox = ToMailboxDto(folder, specialUses);
				upserted[providerMailboxId] = mailbox;
				removed.Remove(providerMailboxId);
				if (streams.TryGetValue(providerMailboxId, out var stream))
				{
					streams[providerMailboxId] = stream with
					{
						ParentProviderMailboxId = mailbox.ParentProviderMailboxId,
					};
				}
			}
			catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				// A removed delta entry is also how Graph reports a move between independent
				// parent streams. Only a point-read 404 proves the immutable folder id gone.
				upserted.Remove(providerMailboxId);
				removed.Add(providerMailboxId);
				foreach (var child in streams.Where(entry =>
					entry.Value.ParentProviderMailboxId == providerMailboxId))
				{
					// Graph may report only the deleted ancestor. Resolve each former child:
					// it may have moved out and must retain its stable local identity.
					pending.Push(child.Key);
				}
			}
		}
	}

	private static bool IsUpsert(MailFolder folder) =>
		folder.Id is not null
		&& folder.DisplayName is not null
		&& folder.AdditionalData?.ContainsKey("@removed") != true;

	private static MailboxDto ToMailboxDto(
		MailFolder folder,
		IReadOnlyDictionary<string, SpecialUse> specialUses
	) =>
		new()
		{
			ProviderMailboxId = folder.Id!,
			Name = folder.DisplayName!,
			ParentProviderMailboxId = folder.ParentFolderId,
			SpecialUse = specialUses.GetValueOrDefault(folder.Id!, SpecialUse.None),
			IsSubscribed = true,
			TotalCount = folder.TotalItemCount,
			UnreadCount = folder.UnreadItemCount,
		};

	private static IReadOnlyList<KeyValuePair<string, GraphTopologyStreamCursor>> OrderStreamsByDepth(
		IReadOnlyDictionary<string, GraphTopologyStreamCursor> streams
	) =>
		[
			.. streams.OrderBy(entry =>
				entry.Key == RootTopologyStream ? -1 : StreamDepth(entry.Key, streams)
			),
		];

	private static int StreamDepth(
		string streamId,
		IReadOnlyDictionary<string, GraphTopologyStreamCursor> streams
	)
	{
		var depth = 0;
		var current = streamId;
		var visited = new HashSet<string>(StringComparer.Ordinal);
		while (current != RootTopologyStream
			&& visited.Add(current)
			&& streams.TryGetValue(current, out var stream)
			&& stream.ParentProviderMailboxId is { } parent)
		{
			depth++;
			current = parent;
		}
		return depth;
	}

	private static bool HasRemovedAncestor(
		string streamId,
		IReadOnlyDictionary<string, GraphTopologyStreamCursor> streams,
		IReadOnlySet<string> removed
	)
	{
		var current = streamId;
		var visited = new HashSet<string>(StringComparer.Ordinal);
		while (current != RootTopologyStream && visited.Add(current))
		{
			if (removed.Contains(current))
			{
				return true;
			}
			if (!streams.TryGetValue(current, out var stream)
				|| stream.ParentProviderMailboxId is not { } parent)
			{
				return false;
			}
			current = parent;
		}
		return false;
	}

	private static void PruneRemovedStreams(
		Dictionary<string, GraphTopologyStreamCursor> streams,
		HashSet<string> removed
	)
	{
		bool changed;
		do
		{
			changed = false;
			foreach (var streamId in streams.Keys.Where(id => id != RootTopologyStream).ToList())
			{
				if (!HasRemovedAncestor(streamId, streams, removed))
				{
					continue;
				}

				removed.Add(streamId);
				streams.Remove(streamId);
				changed = true;
			}
		} while (changed);
	}

	private static GraphTopologyCursor ParseTopologyCursor(string cursor)
	{
		try
		{
			if (!cursor.StartsWith(TopologyCursorPrefix, StringComparison.Ordinal))
			{
				throw new FormatException("Unknown Graph topology cursor version.");
			}

			var payload = cursor[TopologyCursorPrefix.Length..]
				.Replace('-', '+')
				.Replace('_', '/');
			payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
			var parsed = JsonSerializer.Deserialize<GraphTopologyCursor>(
				Convert.FromBase64String(payload)
			);
			if (parsed?.Streams is null
				|| !parsed.Streams.TryGetValue(RootTopologyStream, out var root)
				|| string.IsNullOrWhiteSpace(root.DeltaLink)
				|| parsed.Streams.Any(entry => string.IsNullOrWhiteSpace(entry.Value.DeltaLink)))
			{
				throw new FormatException("Graph topology cursor is incomplete.");
			}
			return parsed;
		}
		catch (Exception ex) when (ex is FormatException or JsonException)
		{
			throw new ProviderCursorInvalidException("Graph mailbox topology cursor is invalid.", ex);
		}
	}

	private static string SerializeTopologyCursor(GraphTopologyCursor cursor)
	{
		var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor))
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
		return TopologyCursorPrefix + payload;
	}

	private sealed record GraphTopologyCursor(
		IReadOnlyDictionary<string, GraphTopologyStreamCursor> Streams
	);

	private sealed record GraphTopologyStreamCursor(
		string DeltaLink,
		string? ParentProviderMailboxId
	);

	private sealed record GraphTopologyChanges(
		IReadOnlyDictionary<string, MailboxDto> Upserted,
		IReadOnlySet<string> Removed
	);

	private sealed record GraphTopologyPage(
		IReadOnlyList<MailFolder> Folders,
		string? NextLink,
		string? DeltaLink
	);
}
