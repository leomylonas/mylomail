using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Bulk `.eml` export (§13 Export): walks an account's mailbox tree, recreating the folder
/// hierarchy on disk from <see cref="Mailbox.ParentId"/>/<see cref="Mailbox.Name"/>, writing
/// each message occurrence as a `.eml` file. A Gmail message in five labels is written into
/// five folders — the correct behaviour for a folder-oriented export.
/// </summary>
/// <remarks>
/// <para>
/// Progress is persisted on <see cref="ExportJob"/> rather than being the batch's own state, so
/// a crash mid-export resumes from <see cref="ExportJob.ResumeToken"/> at startup instead of
/// starting over — the same coverage-state pattern used elsewhere (§6 table).
/// </para>
/// <para>
/// Self-scheduling in batches, like every other bounded-memory job here: each run enqueues its
/// own successor rather than walking the whole account in one call.
/// </para>
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class ExportJobs(
	MyloMailDbContext context,
	ContentAcquisition content,
	IHubEvents events,
	IBackgroundJobClient jobs,
	TimeProvider clock,
	ILogger<ExportJobs> logger
)
{
	private const int BatchSize = 25;

	public async Task<Guid> StartAsync(Guid accountId, string destinationPath, CancellationToken ct = default)
	{
		var mailboxIds = context.Mailboxes.Where(m => m.AccountId == accountId).Select(m => m.Id);
		var occurrenceIds = await context
			.MessageMailboxes.Where(o => mailboxIds.Contains(o.MailboxId))
			.OrderBy(o => o.Id)
			.Select(o => o.Id)
			.ToListAsync(ct);

		var job = new ExportJob
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			DestinationPath = destinationPath,
			TotalCount = occurrenceIds.Count,
			ManifestJson = JsonSerializer.Serialize(occurrenceIds),
			CreatedAt = clock.GetUtcNow(),
		};
		context.ExportJobs.Add(job);
		await context.SaveChangesAsync(ct);

		jobs.Enqueue<ExportJobs>(j => j.RunBatchAsync(job.Id, default));
		return job.Id;
	}

	/// <summary>
	/// The account's most recent export, whatever its status — lets a settings/export screen
	/// reopened mid-export (or after one finished) show where it left off, rather than only
	/// ever seeing progress broadcast live while it happened to be open.
	/// </summary>
	public Task<ExportJob?> GetLatestAsync(Guid accountId, CancellationToken ct = default) =>
		context
			.ExportJobs.Where(j => j.AccountId == accountId)
			.OrderByDescending(j => j.CreatedAt)
			.FirstOrDefaultAsync(ct);

	/// <summary>
	/// Compare-and-swap, the same convention used for scheduled-send cancellation (§13): only a
	/// still-running job can be asked to stop, and the running batch is what actually stops it,
	/// not this call.
	/// </summary>
	public async Task RequestCancelAsync(Guid exportId, CancellationToken ct = default)
	{
		await context
			.ExportJobs.Where(j => j.Id == exportId && j.Status == ExportJobStatus.Running)
			.ExecuteUpdateAsync(u => u.SetProperty(j => j.Status, ExportJobStatus.CancelRequested), ct);
	}

	public async Task RunBatchAsync(Guid exportId, CancellationToken ct = default)
	{
		var job = await context.ExportJobs.FirstOrDefaultAsync(j => j.Id == exportId, ct);
		if (job is null || job.Status is not (ExportJobStatus.Running or ExportJobStatus.CancelRequested))
		{
			return;
		}

		if (job.Status == ExportJobStatus.CancelRequested)
		{
			job.Status = ExportJobStatus.Cancelled;
			await context.SaveChangesAsync(ct);
			await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
			return;
		}

		var account = await context.Accounts.FirstAsync(a => a.Id == job.AccountId, ct);

		try
		{
			var mailboxes = await context.Mailboxes.Where(m => m.AccountId == job.AccountId).ToListAsync(ct);
			var folders = BuildFolderPaths(mailboxes, job.DestinationPath);

			var manifest = JsonSerializer.Deserialize<List<Guid>>(job.ManifestJson) ?? [];
			var pageIds = manifest.Skip(job.ResumeToken).Take(BatchSize).ToList();
			if (pageIds.Count == 0)
			{
				job.Status = ExportJobStatus.Completed;
				await context.SaveChangesAsync(ct);
				await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
				return;
			}

			// Re-fetched by the frozen ids rather than re-walking `MessageMailboxes` live: the
			// manifest is what defines this batch, the table only supplies each row's content.
			var batchById = await context
				.MessageMailboxes.Where(o => pageIds.Contains(o.Id))
				.ToDictionaryAsync(o => o.Id, ct);

			var contentDeferred = false;

			foreach (var occurrenceId in pageIds)
			{
				// A manifest entry can point at a row that no longer exists — the message was
				// moved or deleted after the manifest was frozen. That is simply one fewer file
				// written, not a failure: the manifest describes what to attempt, not a
				// guarantee every entry still resolves.
				if (
					batchById.TryGetValue(occurrenceId, out var occurrence)
					&& folders.TryGetValue(occurrence.MailboxId, out var folder)
				)
				{
					Directory.CreateDirectory(folder);
					try
					{
						var raw = await RawBytesAsync(account, occurrence.MessageId, ct);
						await File.WriteAllBytesAsync(
							Path.Combine(folder, $"{occurrence.MessageId:N}.eml"),
							raw,
							ct
						);
						job.WrittenCount++;
					}
					catch (ContentAcquisitionDeferredException)
					{
						// The occurrence changed while its provider call was in flight. Keep
						// this manifest position until a fresh incarnation can supply content;
						// advancing would permanently omit the message from this export.
						contentDeferred = true;
						break;
					}
					catch (Exception ex)
					{
						// One unreadable message must not abandon the rest of the export — the
						// user gets everything else, plus the last error, rather than nothing.
						job.LastError = ex.Message;
						logger.LogWarning(
							ex,
							"Export {ExportId} could not write message {MessageId}.",
							job.Id,
							occurrence.MessageId
						);
					}
				}

				job.ResumeToken++;
			}

			if (contentDeferred)
			{
				await context.SaveChangesAsync(ct);
				jobs.Schedule<ExportJobs>(
					j => j.RunBatchAsync(job.Id, default),
					TimeSpan.FromSeconds(1)
				);
				return;
			}

			// Fewer than a full page of manifest entries means this was the tail: no point
			// enqueueing a successor just to discover the walk is already empty.
			if (pageIds.Count < BatchSize)
			{
				job.Status = ExportJobStatus.Completed;
			}

			await context.SaveChangesAsync(ct);
			await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
			if (job.Status == ExportJobStatus.Running)
			{
				jobs.Enqueue<ExportJobs>(j => j.RunBatchAsync(job.Id, default));
			}
		}
		catch (Exception ex)
		{
			job.Status = ExportJobStatus.Failed;
			job.LastError = ex.Message;
			await context.SaveChangesAsync(ct);
			await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
			logger.LogWarning(ex, "Export {ExportId} failed.", job.Id);
		}
	}

	/// <summary>The stored MIME bytes, fetched on demand if content acquisition hasn't reached it yet.</summary>
	public async Task<byte[]> RawBytesAsync(Account account, Guid messageId, CancellationToken ct = default)
	{
		var existing = await context.MessageRaws.FirstOrDefaultAsync(r => r.MessageId == messageId, ct);
		if (existing is not null)
		{
			return existing.Content;
		}

		if (await content.AcquireAsync(account, messageId, ct) == ContentAcquisitionResult.Deferred)
		{
			throw new ContentAcquisitionDeferredException(messageId);
		}
		var fetched = await context.MessageRaws.SingleAsync(r => r.MessageId == messageId, ct);
		return fetched.Content;
	}

	/// <summary>Recreates the mailbox tree as a folder tree, each name sanitised as a path segment.</summary>
	private static Dictionary<Guid, string> BuildFolderPaths(IReadOnlyList<Mailbox> mailboxes, string root)
	{
		var byId = mailboxes.ToDictionary(m => m.Id);
		var paths = new Dictionary<Guid, string>();

		string PathFor(Guid id)
		{
			if (paths.TryGetValue(id, out var known))
			{
				return known;
			}

			var mailbox = byId[id];
			var segment = AttachmentTempDirectory.SanitiseFilename(mailbox.Name);
			var parent =
				mailbox.ParentId is Guid parentId && byId.ContainsKey(parentId) ? PathFor(parentId) : root;
			var path = Path.Combine(parent, segment);
			paths[id] = path;
			return path;
		}

		foreach (var mailbox in mailboxes)
		{
			PathFor(mailbox.Id);
		}

		return paths;
	}
}

internal sealed class ContentAcquisitionDeferredException(Guid messageId)
	: Exception($"Content acquisition for message '{messageId}' was deferred.")
{ }
