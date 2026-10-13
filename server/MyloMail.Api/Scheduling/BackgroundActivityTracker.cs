using System.Collections.Concurrent;
using Hangfire.Common;
using Hangfire.Server;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// What background work is running right now, so the status bar can say what and how far along.
/// </summary>
/// <remarks>
/// In memory and only about this process's running work: nothing here is state, and nothing may be
/// decided from it. Work that is durable — queued changes, exports — is read from its own rows by
/// <c>GetBackgroundTasks</c>, so it still shows after a restart.
/// </remarks>
public sealed class BackgroundActivityTracker(TimeProvider clock)
{
	private readonly ConcurrentDictionary<string, Entry> running = new();

	public sealed class Entry(string label, Guid? accountId, DateTimeOffset startedAt)
	{
		public string Label { get; } = label;
		public Guid? AccountId { get; } = accountId;
		public DateTimeOffset StartedAt { get; } = startedAt;
		public int? Done { get; set; }
		public int? Total { get; set; }
	}

	/// <summary>Starts reporting a piece of work; dispose the result when it ends, however it ends.</summary>
	public ActivityHandle Begin(string label, Guid? accountId = null)
	{
		var id = Guid.NewGuid().ToString("N");
		var entry = new Entry(label, accountId, clock.GetUtcNow());
		running[id] = entry;
		return new ActivityHandle(this, id, entry);
	}

	internal void Start(string key, string label, Guid? accountId) =>
		running[key] = new Entry(label, accountId, clock.GetUtcNow());

	internal void Stop(string key) => running.TryRemove(key, out _);

	public IReadOnlyList<(string Id, Entry Entry)> Snapshot() =>
		[.. running.Select(pair => (pair.Key, pair.Value)).OrderBy(pair => pair.Value.StartedAt)];

	public sealed class ActivityHandle(BackgroundActivityTracker owner, string id, Entry entry) : IDisposable
	{
		public void Report(int done, int? total)
		{
			entry.Done = done;
			entry.Total = total;
		}

		public void Dispose() => owner.Stop(id);
	}
}

/// <summary>
/// Reports the Hangfire jobs a user would want to see as running, without each job having to say
/// so. The set is explicit: loops that wake every few seconds to find nothing, and work with its
/// own, richer reporting, stay out.
/// </summary>
public sealed class BackgroundActivityFilter(BackgroundActivityTracker tracker) : IServerFilter
{
	private static readonly Dictionary<(string Type, string Method), string> Labels = new()
	{
		[("SyncJobs", "TopologyAsync")] = "Reading the folder list",
		[("SyncJobs", "CalendarAsync")] = "Syncing the calendar",
		[("SyncJobs", "IntegrityAsync")] = "Checking a folder against the server",
		[("SyncJobs", "ReplayStagedAsync")] = "Applying changes received during the download",
		[("SyncJobs", "RunSyncNowAsync")] = "Send / receive",
		[("ContactJobs", "RefreshAsync")] = "Refreshing contacts",
		[("ContactJobs", "ExecuteAsync")] = "Saving a contact change",
		[("DraftJobs", "PushAsync")] = "Saving drafts to the server",
		[("OutboxJobs", "RunAsync")] = "Sending mail",
		[("TombstoneGcJobs", "SweepAsync")] = "Cleaning up deleted messages",
	};

	public void OnPerforming(PerformingContext context)
	{
		var job = context.BackgroundJob.Job;
		if (!Labels.TryGetValue((job.Type.Name, job.Method.Name), out var label))
		{
			return;
		}

		var accountId = job.Args.OfType<Guid>().Cast<Guid?>().FirstOrDefault();
		tracker.Start(context.BackgroundJob.Id, label, accountId);
	}

	public void OnPerformed(PerformedContext context) => tracker.Stop(context.BackgroundJob.Id);
}
