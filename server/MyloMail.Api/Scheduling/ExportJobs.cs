using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Credentials;
using MyloMail.Api.Domain;
using MyloMail.Api.Export;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

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
[Queue(ExportJobs.QueueName)]
public sealed class ExportJobs(
	MyloMailDbContext context,
	ContentAcquisition content,
	AccountGate gate,
	ConnectivityMonitor connectivity,
	IHubEvents events,
	IBackgroundJobClient jobs,
	TimeProvider clock,
	ILogger<ExportJobs> logger
)
{
	private const int BatchSize = 25;
	private static readonly TimeSpan CredentialStoreRetryDelay = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Exports run on a queue and a single worker of their own, so a long export never occupies
	/// a worker that sync, sending or another account's jobs are waiting for.
	/// </summary>
	public const string QueueName = "export";

	public async Task<Guid> StartAsync(
		Guid accountId,
		IReadOnlyCollection<Guid> mailboxIds,
		ExportFormat format,
		ExportFileNaming fileNaming,
		string destinationPath,
		CancellationToken ct = default
	)
	{
		var occurrenceIds = await OccurrencesAsync(mailboxIds, ct);

		var job = new ExportJob
		{
			Id = Guid.NewGuid(),
			AccountId = accountId,
			DestinationPath = destinationPath,
			Format = format,
			FileNaming = fileNaming,
			MailboxIdsJson = JsonSerializer.Serialize(mailboxIds),
			TotalCount = occurrenceIds.Count,
			ManifestJson = JsonSerializer.Serialize(occurrenceIds),
			CreatedAt = clock.GetUtcNow(),
		};
		context.ExportJobs.Add(job);
		await context.SaveChangesAsync(ct);

		jobs.Enqueue<ExportJobs>(next => next.RunBatchAsync(job.Id, default));
		return job.Id;
	}

	/// <summary>The occurrences in these mailboxes, by mailbox and then date, which is also the order an mbox file is written in.</summary>
	private async Task<List<Guid>> OccurrencesAsync(IReadOnlyCollection<Guid> mailboxIds, CancellationToken ct)
	{
		// Ordered here rather than in SQL: SQLite cannot order by a DateTimeOffset.
		var rows = await context
			.MessageMailboxes.Where(o => mailboxIds.Contains(o.MailboxId))
			.Join(context.Messages, o => o.MessageId, m => m.Id, (o, m) => new { o.Id, o.MailboxId, m.ReceivedAt })
			.ToListAsync(ct);
		return [.. rows.OrderBy(x => x.MailboxId).ThenBy(x => x.ReceivedAt).ThenBy(x => x.Id).Select(x => x.Id)];
	}

	/// <summary>
	/// Every export, newest first, so the wizard can find the one it should show. The manifest, which
	/// can be megabytes, is left out; SQLite cannot order by a DateTimeOffset, so that is done here.
	/// </summary>
	public async Task<List<ExportJob>> GetRecentAsync(int count, CancellationToken ct = default)
	{
		var jobs = await context
			.ExportJobs.AsNoTracking()
			.Select(j => new ExportJob
			{
				Id = j.Id,
				AccountId = j.AccountId,
				DestinationPath = j.DestinationPath,
				Status = j.Status,
				TotalCount = j.TotalCount,
				WrittenCount = j.WrittenCount,
				SkippedCount = j.SkippedCount,
				Format = j.Format,
				LastError = j.LastError,
				CreatedAt = j.CreatedAt,
				CompletedAt = j.CompletedAt,
			})
			.ToListAsync(ct);
		return [.. jobs.OrderByDescending(j => j.CreatedAt).Take(count)];
	}

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

	/// <remarks>
	/// A batch is not gated merely because the app is offline: cached MIME must remain
	/// exportable without a network. The first cache miss pauses at the provider boundary and
	/// retains this exact manifest position for connectivity recovery.
	/// </remarks>
	public async Task RunBatchAsync(Guid exportId, CancellationToken ct = default)
	{
		var workKey = $"{nameof(RunBatchAsync)}:{exportId}";

		var job = await context.ExportJobs.FirstOrDefaultAsync(j => j.Id == exportId, ct);
		if (job is null || job.Status is not (ExportJobStatus.Running or ExportJobStatus.CancelRequested))
		{
			return;
		}

		if (job.Status == ExportJobStatus.CancelRequested)
		{
			job.Status = ExportJobStatus.Cancelled;
			job.CompletedAt = clock.GetUtcNow();
			if (job.Format == ExportFormat.Pst)
			{
				PstExportSession.Discard(job.DestinationPath);
			}

			await context.SaveChangesAsync(ct);
			await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
			return;
		}

		var account = await context.Accounts.FirstAsync(a => a.Id == job.AccountId, ct);

		PstExportSession? pst = null;
		try
		{
			var mailboxes = await context.Mailboxes.Where(m => m.AccountId == job.AccountId).ToListAsync(ct);
			var folders = ExportLayout.Build(mailboxes, job.DestinationPath, job.Format);

			var manifest = JsonSerializer.Deserialize<List<Guid>>(job.ManifestJson) ?? [];

			if (job.Format == ExportFormat.Pst)
			{
				pst = PstExportSession.Open(
					job.DestinationPath,
					account.DisplayName,
					mailboxes,
					JsonSerializer.Deserialize<List<Guid>>(job.MailboxIdsJson) ?? []
				);
				if (pst is null)
				{
					// The PST was finished and committed; only the database did not hear of it.
					job.Status = ExportJobStatus.Completed;
					job.CompletedAt = clock.GetUtcNow();
					await context.SaveChangesAsync(ct);
					await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
					return;
				}

				// The writer's checkpoint is the truth about what is in the file.
				job.ResumeToken = pst.State.ResumeToken;
				job.WrittenCount = pst.State.Written;
				job.SkippedCount = pst.State.Skipped;
			}

			var pageIds = manifest.Skip(job.ResumeToken).Take(BatchSize).ToList();
			if (pageIds.Count == 0)
			{
				await ExtendOrCompleteAsync(job, manifest, pst, ct);
				return;
			}

			// Re-fetched by the frozen ids rather than re-walking `MessageMailboxes` live: the
			// manifest is what defines this batch, the table only supplies each row's content.
			var batchById = await context
				.MessageMailboxes.Where(o => pageIds.Contains(o.Id))
				.ToDictionaryAsync(o => o.Id, ct);

			var messageIds = batchById.Values.Select(o => o.MessageId).ToList();
			var messagesById = await context
				.Messages.AsNoTracking()
				.Where(m => messageIds.Contains(m.Id))
				.ToDictionaryAsync(m => m.Id, ct);
			var offsets =
				JsonSerializer.Deserialize<Dictionary<Guid, long>>(job.MboxOffsetsJson) ?? [];
			var truncated = new HashSet<Guid>();

			var contentDeferred = false;
			var connectivityPaused = false;
			TimeSpan? providerRetryAfter = null;

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
					byte[] raw;
					try
					{
						raw = await RawBytesAsync(account, occurrence.MessageId, ct);
					}
					catch (ContentAcquisitionDeferredException)
					{
						// The occurrence changed while its provider call was in flight. Keep
						// this manifest position until a fresh incarnation can supply content;
						// advancing would permanently omit the message from this export.
						contentDeferred = true;
						break;
					}
					catch (ProviderThrottledException ex)
					{
						providerRetryAfter = ex.RetryAfter;
						gate.Throttle(account.Id, ex.RetryAfter);
						break;
					}
					catch (CredentialStoreUnavailableException ex)
					{
						account.AuthState = AuthState.CredentialStoreUnavailable;
						account.LastAuthError = ex.Message;
						await context.SaveChangesAsync(ct);
						await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
						jobs.Schedule<ExportJobs>(
							next => next.RunBatchAsync(job.Id, default),
							CredentialStoreRetryDelay
						);
						return;
					}
					catch (ProviderAuthenticationException ex)
					{
						account.AuthState = ex.AccountState;
						account.LastAuthError = ex.Message;
						await context.SaveChangesAsync(ct);
						await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
						return;
					}
					catch (ProviderNotConfiguredException ex)
					{
						account.AuthState = AuthState.Error;
						account.LastAuthError = ex.Message;
						await context.SaveChangesAsync(ct);
						await Accounts.AccountDtoFactory.AnnounceStatusAsync(context, events, account, ct);
						return;
					}
					catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
					{
						connectivityPaused = true;
						await connectivity.PauseAsync(
							workKey,
							client => client.Enqueue<ExportJobs>(
								next => next.RunBatchAsync(exportId, default)
							)
						);
						break;
					}

					try
					{
						messagesById.TryGetValue(occurrence.MessageId, out var message);
						if (job.Format == ExportFormat.Mbox)
						{
							var path = folder;
							Directory.CreateDirectory(Path.GetDirectoryName(path)!);
							// Cut the file back to what the last saved position accounts for, once per
							// batch: a crash after appending but before saving would otherwise repeat
							// the message on resume.
							if (truncated.Add(occurrence.MailboxId) && File.Exists(path))
							{
								await using var existing = new FileStream(path, FileMode.Open, FileAccess.Write);
								var keep = offsets.GetValueOrDefault(occurrence.MailboxId);
								if (existing.Length > keep)
								{
									existing.SetLength(keep);
								}
							}

							await using var mbox = new FileStream(path, FileMode.Append, FileAccess.Write);
							MboxFormat.Append(
								mbox,
								message?.SenderAddress?.Email ?? message?.From.FirstOrDefault()?.Email ?? string.Empty,
								message?.ReceivedAt ?? DateTimeOffset.UnixEpoch,
								raw
							);
							await mbox.FlushAsync(ct);
							offsets[occurrence.MailboxId] = mbox.Length;
						}
						else if (job.Format == ExportFormat.Pst)
						{
							pst!.AddMessage(occurrence.MailboxId, raw);
						}
						else
						{
							Directory.CreateDirectory(folder);
							var stem =
								job.FileNaming == ExportFileNaming.DateAndSubject && message is not null
									? ExportNames.DateAndSubject(message.ReceivedAt, message.Subject)
									: occurrence.MessageId.ToString("N");
							await WriteEmlAsync(folder, stem, occurrence.MessageId, raw, ct);
						}

						job.WrittenCount++;
					}
					catch (Exception ex)
					{
						// A local path failure must not masquerade as offline just because
						// network transports and File APIs both use IOException.
						job.LastError = ex.Message;
						job.SkippedCount++;
						logger.LogWarning(
							ex,
							"Export {ExportId} could not write message {MessageId}.",
							job.Id,
							occurrence.MessageId
						);
					}
				}
				else
				{
					job.SkippedCount++;
				}

				job.ResumeToken++;
			}
			if (providerRetryAfter is { } retryAfter)
			{
				await context.SaveChangesAsync(ct);
				await Accounts.AccountDtoFactory.AnnounceStatusAsync(
					context,
					events,
					account,
					ct,
					gate
				);
				jobs.Schedule<ExportJobs>(
					next => next.RunBatchAsync(job.Id, default),
					retryAfter
				);
				return;
			}

			if (connectivityPaused)
			{
				await context.SaveChangesAsync(ct);
				return;
			}


			if (contentDeferred)
			{
				await context.SaveChangesAsync(ct);
				connectivity.DispatchOrDefer(
					workKey,
					client => client.Schedule<ExportJobs>(
						next => next.RunBatchAsync(job.Id, default),
						TimeSpan.FromSeconds(1)
					)
				);
				return;
			}

			job.MboxOffsetsJson = JsonSerializer.Serialize(offsets);
			pst?.Checkpoint(job.ResumeToken, job.WrittenCount, job.SkippedCount);
			if (job.ResumeToken >= manifest.Count)
			{
				await ExtendOrCompleteAsync(job, manifest, pst, ct);
				return;
			}

			await context.SaveChangesAsync(ct);
			await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
			jobs.Enqueue<ExportJobs>(next => next.RunBatchAsync(job.Id, default));
		}
		catch (Exception ex) when (ConnectivityMonitor.IsNetworkFailure(ex))
		{
			await connectivity.PauseAsync(
				workKey,
				client => client.Enqueue<ExportJobs>(
					job => job.RunBatchAsync(exportId, default)
				)
			);
		}
		catch (Exception ex)
		{
			job.Status = ExportJobStatus.Failed;
			job.CompletedAt = clock.GetUtcNow();
			job.LastError = ex.Message;
			if (job.Format == ExportFormat.Pst)
			{
				pst?.Dispose();
				pst = null;
				PstExportSession.Discard(job.DestinationPath);
			}

			await context.SaveChangesAsync(ct);
			await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
			logger.LogWarning(ex, "Export {ExportId} failed.", job.Id);
		}
		finally
		{
			pst?.Dispose();
		}
	}

	/// <summary>
	/// The manifest is spent. Mail that arrived in the chosen folders while it was being written is
	/// added and written too; the export is complete only when a pass over those folders finds
	/// nothing it has not already seen, and mail arriving after that is not part of it.
	/// </summary>
	private async Task ExtendOrCompleteAsync(ExportJob job, List<Guid> manifest, PstExportSession? pst, CancellationToken ct)
	{
		var chosen = JsonSerializer.Deserialize<List<Guid>>(job.MailboxIdsJson) ?? [];
		var seen = manifest.ToHashSet();
		var arrived = (await OccurrencesAsync(chosen, ct)).Where(id => !seen.Contains(id)).ToList();
		if (arrived.Count > 0)
		{
			manifest.AddRange(arrived);
			job.ManifestJson = JsonSerializer.Serialize(manifest);
			job.TotalCount = manifest.Count;
			job.CatchUpPasses++;
		}
		else
		{
			// Commits the PST: its indexes are written and the file is put in place atomically.
			pst?.Complete();
			job.Status = ExportJobStatus.Completed;
			job.CompletedAt = clock.GetUtcNow();
		}

		await context.SaveChangesAsync(ct);
		await events.ExportProgressAsync(job.Id, job.WrittenCount, job.TotalCount);
		if (job.Status == ExportJobStatus.Running)
		{
			jobs.Enqueue<ExportJobs>(next => next.RunBatchAsync(job.Id, default));
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
		var delay = gate.Delay(account.Id);
		if (delay > TimeSpan.Zero)
		{
			throw new ProviderThrottledException(delay, "Account is throttled.");
		}


		if (await content.AcquireAsync(account, messageId, ct) == ContentAcquisitionResult.Deferred)
		{
			throw new ContentAcquisitionDeferredException(messageId);
		}
		var fetched = await context.MessageRaws.SingleAsync(r => r.MessageId == messageId, ct);
		return fetched.Content;
	}

	/// <summary>
	/// Writes <c>stem.eml</c> in <paramref name="folder"/>. A name already taken by different bytes
	/// (two messages with the same date and subject) gets the message id appended; one taken by the
	/// same bytes is this message, written before a restart, and is left as it is.
	/// </summary>
	private static async Task WriteEmlAsync(string folder, string stem, Guid messageId, byte[] raw, CancellationToken ct)
	{
		var path = Path.Combine(folder, $"{stem}.eml");
		if (File.Exists(path))
		{
			if (new FileInfo(path).Length == raw.Length && (await File.ReadAllBytesAsync(path, ct)).AsSpan().SequenceEqual(raw))
			{
				return;
			}

			path = Path.Combine(folder, $"{stem} [{messageId:N}].eml");
		}

		await File.WriteAllBytesAsync(path, raw, ct);
	}
}

internal sealed class ContentAcquisitionDeferredException(Guid messageId)
	: Exception($"Content acquisition for message '{messageId}' was deferred.")
{ }
