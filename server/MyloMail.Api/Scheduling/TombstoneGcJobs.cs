using Hangfire;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Content;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Scheduling;

/// <summary>
/// Reclaims tombstoned messages (§6): a message with no remaining mailbox membership persists
/// until nothing else references it, then is physically deleted.
/// </summary>
/// <remarks>
/// <para>
/// Physical deletion is a garbage-collection decision based on references, not something a
/// mutation worker performs when its own chain goes terminal — this is why <see
/// cref="MutationItem.MessageId"/> carries no foreign key. The references checked here are
/// exactly the ones §6 names: a live (non-terminal) mutation, an undelivered notification whose
/// click-to-navigate still needs the row, and a draft's reply linkage.
/// </para>
/// <para>
/// <b>Zero membership is not immediately collectible.</b> Under Graph's folder-scoped delta a
/// move surfaces as a source-removal and a destination-addition in either order, possibly
/// minutes apart, leaving a canonical message transiently membership-less (§3). Collection
/// waits <see cref="GracePeriod"/> from the moment it first notices a message orphaned — <see
/// cref="Message.OrphanedAt"/> — so a late destination delta always finds the row still there.
/// </para>
/// <para>
/// One message per pass, enqueuing its own successor immediately while it is still finding work
/// and backing off once a sweep finds none, so a large backlog is reclaimed gradually rather
/// than in one long-running scan (§6, "Job state is bounded by memory").
/// </para>
/// </remarks>
[AutomaticRetry(Attempts = 0)]
public sealed class TombstoneGcJobs(
	MyloMailDbContext context,
	SearchIndexer search,
	TimeProvider clock,
	IBackgroundJobClient jobs,
	ILogger<TombstoneGcJobs> logger
)
{
	private const int CandidateBatchLimit = 50;

	/// <summary>
	/// How long a message must stay continuously orphaned before it is eligible for
	/// collection — longer than any plausible gap between a Graph move's two deltas (§3).
	/// </summary>
	private static readonly TimeSpan GracePeriod = TimeSpan.FromMinutes(30);

	/// <summary>
	/// Idle back-off once a sweep finds nothing collectible. Long enough that a standing loop
	/// per account costs nothing worth noticing; short enough that a tombstone whose grace
	/// period or last reference clears is reclaimed promptly rather than lingering until the
	/// next restart.
	/// </summary>
	private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(10);

	public async Task SweepAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return;
		}

		var collected = await CollectOneAsync(accountId, ct);
		jobs.Schedule<TombstoneGcJobs>(
			job => job.SweepAsync(accountId, default),
			collected ? TimeSpan.Zero : IdleDelay
		);
	}

	/// <summary>Finds and reclaims at most one tombstone, returning whether it found one.</summary>
	private async Task<bool> CollectOneAsync(Guid accountId, CancellationToken ct)
	{
		var now = clock.GetUtcNow();
		var candidates = await context
			.Messages.Where(m => m.AccountId == accountId)
			.Where(m => !context.MessageMailboxes.Any(o => o.MessageId == m.Id))
			.Select(m => new { m.Id, m.OrphanedAt })
			.Take(CandidateBatchLimit)
			.ToListAsync(ct);

		foreach (var candidate in candidates)
		{
			if (candidate.OrphanedAt is null)
			{
				// First time this message has been seen with no membership at all — start the
				// grace period rather than acting on it now.
				await MarkOrphanedAsync(candidate.Id, now, ct);
				continue;
			}

			if (now - candidate.OrphanedAt.Value < GracePeriod)
			{
				continue;
			}

			if (await TryCollectAsync(candidate.Id, ct))
			{
				logger.LogInformation("Collected tombstoned message {MessageId}.", candidate.Id);
				return true;
			}
		}

		return false;
	}

	private async Task MarkOrphanedAsync(Guid messageId, DateTimeOffset now, CancellationToken ct)
	{
		var message = await context.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
		if (message is null || message.OrphanedAt is not null)
		{
			return;
		}

		message.OrphanedAt = now;
		await context.SaveChangesAsync(ct);
	}

	private async Task<bool> IsCollectibleAsync(Guid messageId, CancellationToken ct)
	{
		var hasLiveMutation = await context.MutationItems.AnyAsync(
			m =>
				m.MessageId == messageId
				&& m.State != MutationState.Completed
				&& m.State != MutationState.Failed
				&& m.State != MutationState.Cancelled,
			ct
		);
		if (hasLiveMutation)
		{
			return false;
		}

		var hasPendingChange = await context.MessagePendingChanges.AnyAsync(
			c => c.MessageId == messageId,
			ct
		);
		if (hasPendingChange)
		{
			return false;
		}

		var hasUndeliveredNotification = await context.NotificationRecords.AnyAsync(
			n => n.MessageId == messageId && n.DeliveredAt == null,
			ct
		);
		if (hasUndeliveredNotification)
		{
			return false;
		}

		var hasReplyLinkage = await context.Drafts.AnyAsync(d => d.InReplyToMessageId == messageId, ct);
		return !hasReplyLinkage;
	}

	/// <summary>
	/// Re-checks eligibility and deletes in the same transaction, immediately adjacent to each
	/// other — narrowing, though not eliminating, the window in which a concurrent draft save
	/// or mutation enqueue (both reference <see cref="Domain.Message.Id"/> without an
	/// enforced foreign key, precisely so a tombstone may still exist when they run) could
	/// land between an eligibility check and the delete it was supposed to gate.
	/// </summary>
	private async Task<bool> TryCollectAsync(Guid messageId, CancellationToken ct)
	{
		await using var transaction = await context.Database.BeginTransactionAsync(ct);

		if (!await IsCollectibleAsync(messageId, ct))
		{
			await transaction.RollbackAsync(ct);
			return false;
		}

		var message = await context.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
		if (message is null)
		{
			await transaction.RollbackAsync(ct);
			return false;
		}

		// The FTS5 external-content row must go in the same operation as the message row it
		// mirrors, or search returns hits pointing at nothing (§6). Its own foreign key is
		// Restrict, not Cascade, for exactly this reason — removed here first rather than relied
		// on to cascade.
		await search.RemoveAsync(messageId, ct);

		context.Messages.Remove(message);
		await context.SaveChangesAsync(ct);

		await transaction.CommitAsync(ct);
		return true;
	}
}
