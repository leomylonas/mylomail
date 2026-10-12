using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MyloMail.Api.Content;

/// <summary>
/// Message documents the renderer has asked the backend to serve, each under an unguessable id.
/// </summary>
/// <remarks>
/// A message is shown in an iframe. A <c>srcdoc</c> frame inherits the app page's content policy,
/// which forbids inline styles and so breaks nearly every email; a frame loaded from a URL gets
/// only the policy the response carries. The renderer prepares the (already sanitised) document,
/// hands it here, and loads the frame from <c>/message-frames/{id}</c>, where
/// <see cref="Controllers.MessageFramesController"/> serves it under its own policy.
/// <para>
/// In memory and short-lived by design: a document is derived from stored mail and is re-sent
/// whenever the renderer needs it, so nothing here is worth persisting, and bounding both age and
/// count keeps opened messages from accumulating.
/// </para>
/// </remarks>
public sealed class MessageFrameStore(TimeProvider clock)
{
	public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
	public const int MaxEntries = 64;

	private readonly ConcurrentDictionary<string, Entry> entries = new();

	public string Add(string document, bool allowRemote)
	{
		Prune();
		var id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
			.Replace('+', '-')
			.Replace('/', '_')
			.TrimEnd('=');
		entries[id] = new Entry(document, allowRemote, clock.GetUtcNow() + Lifetime);
		return id;
	}

	public (string Document, bool AllowRemote)? Get(string id) =>
		entries.TryGetValue(id, out var entry) && entry.Expires > clock.GetUtcNow()
			? (entry.Document, entry.AllowRemote)
			: null;

	private void Prune()
	{
		var now = clock.GetUtcNow();
		foreach (var pair in entries.Where(pair => pair.Value.Expires <= now))
		{
			entries.TryRemove(pair.Key, out _);
		}

		// Past the cap the oldest go first: the renderer re-sends a document it still needs.
		foreach (var pair in entries.OrderBy(pair => pair.Value.Expires).Take(Math.Max(0, entries.Count - MaxEntries + 1)))
		{
			entries.TryRemove(pair.Key, out _);
		}
	}

	private sealed record Entry(string Document, bool AllowRemote, DateTimeOffset Expires);
}
