using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Notifications;

/// <summary>
/// Notification eligibility, durable recording, and dispatch (§13 Epic 9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Eligibility comes from change-stream provenance, not row creation.</b> A canonical
/// message row can already exist — created earlier by backfill or reconciliation — and still
/// be genuinely new mail the moment the live stream (or, for Gmail, its replayed staged
/// history) reports it: that is exactly the scenario a per-message row-creation rule gets
/// wrong. <see cref="RecordEligibleAsync"/> therefore takes every message this page's
/// upserts touched, created or not, and the per-<c>(account, message, kind)</c> unique
/// constraint this checks against is what makes evaluating the same message more than once
/// harmless rather than merely rare.
/// </para>
/// <para>
/// Two guards, because a single per-account provider baseline does not exist across
/// providers: <paramref name="notificationBaseline"/> is a fixed instant this stream's own
/// <see cref="ChangeStreamState.NotificationBaselineAt"/> captured once, at stream creation or
/// immediately on a triggered resync — <b>before</b> that resync's own catch-up work runs, so
/// mail arriving during the resync window is never classified as predating it (§13 Epic 9:
/// "advancing after would classify mail arriving during the resync window as old and silently
/// drop those notifications"). <see cref="Account.NotificationEpoch"/> is separate,
/// account-wide, local policy: never notify for anything dated before the account was set up,
/// independent of which stream happens to report it — this is what actually excludes an
/// initial backlog for Gmail's staged replay, whose stream baseline is set the moment staging
/// begins rather than when replay eventually runs.
/// </para>
/// </remarks>
public sealed class NotificationService(MyloMailDbContext context, IHubEvents events, TimeProvider clock)
{
	/// <summary>
	/// Adds a tracked, not-yet-saved <see cref="NotificationRecord"/> for each eligible
	/// message not already notified. Callers add these to the same transaction as the message
	/// rows they describe — the record must never persist if the message it refers to did not
	/// (§9).
	/// </summary>
	public async Task<IReadOnlyList<NotificationRecord>> RecordEligibleAsync(
		Account account,
		IReadOnlyList<Message> upserted,
		DateTimeOffset notificationBaseline,
		CancellationToken ct = default
	)
	{
		if (!account.NotificationsEnabled || upserted.Count == 0)
		{
			return [];
		}

		var cutoff = account.NotificationEpoch > notificationBaseline ? account.NotificationEpoch : notificationBaseline;
		var candidates = upserted.Where(m => m.ReceivedAt >= cutoff).ToList();
		if (candidates.Count == 0)
		{
			return [];
		}

		var candidateIds = candidates.Select(m => m.Id).ToList();
		var alreadyNotified = await context
			.NotificationRecords.Where(n =>
				n.AccountId == account.Id && n.Kind == NotificationKind.NewMessage && candidateIds.Contains(n.MessageId)
			)
			.Select(n => n.MessageId)
			.ToListAsync(ct);
		var alreadyNotifiedIds = alreadyNotified.ToHashSet();

		var records = new List<NotificationRecord>();
		foreach (var message in candidates)
		{
			if (alreadyNotifiedIds.Contains(message.Id))
			{
				continue;
			}

			var record = new NotificationRecord
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				MessageId = message.Id,
				Kind = NotificationKind.NewMessage,
				CreatedAt = clock.GetUtcNow(),
			};
			context.NotificationRecords.Add(record);
			records.Add(record);
		}

		return records;
	}

	/// <summary>
	/// Announces already-committed records, using summaries the caller already computed
	/// rather than re-querying for content this soon after the same transaction.
	/// </summary>
	public async Task AnnounceAsync(
		IReadOnlyList<NotificationRecord> records,
		IReadOnlyDictionary<Guid, MessageSummaryDto> summaries
	)
	{
		foreach (var record in records)
		{
			if (summaries.TryGetValue(record.MessageId, out var summary))
			{
				await events.NotificationReadyAsync(ToDto(record, summary));
			}
		}
	}

	/// <summary>
	/// Re-announces every record this account has not yet confirmed delivered — the recovery
	/// path for a crash between a prior announcement and the client's delivery confirmation.
	/// Duplicating an already-shown notification is the accepted cost; silently dropping one
	/// is not (§13 Epic 9).
	/// </summary>
	public async Task RedispatchPendingAsync(Guid accountId, CancellationToken ct = default)
	{
		var pending = await context
			.NotificationRecords.Where(n => n.AccountId == accountId && n.DeliveredAt == null)
			.Join(
				context.Messages,
				n => n.MessageId,
				m => m.Id,
				(n, m) => new { Notification = n, Message = m }
			)
			.ToListAsync(ct);

		foreach (var row in pending)
		{
			await events.NotificationReadyAsync(ToDto(row.Notification, Preview(row.Message)));
		}
	}

	/// <summary>Called once the shell confirms the OS notification was shown at least once.</summary>
	public async Task MarkDeliveredAsync(Guid notificationId, CancellationToken ct = default)
	{
		var record = await context.NotificationRecords.FirstOrDefaultAsync(n => n.Id == notificationId, ct);
		if (record is null)
		{
			return;
		}

		record.DeliveredAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
	}

	private static NotificationDto ToDto(NotificationRecord record, MessageSummaryDto summary) =>
		new(record.Id, record.AccountId, record.MessageId, Sender(summary), summary.Subject);

	private static MessageSummaryDto Preview(Message message) =>
		new(
			message.Id,
			message.AccountId,
			message.Subject,
			string.Empty,
			message.From,
			message.ReceivedAt,
			true,
			false,
			false
		);

	private static string Sender(MessageSummaryDto summary) =>
		summary.From.Count > 0 ? summary.From[0].Name ?? summary.From[0].Email : "New message";
}
