using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Notifications;

/// <summary>
/// Notification eligibility, durable recording, and dispatch (§13 Epic 9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Eligibility comes from change-stream provenance, not row creation.</b> A canonical
/// message row can already exist — created earlier by backfill or reconciliation — and still
/// be genuinely new mail the moment the live stream (or, for Gmail, its staged history)
/// reports it: that is exactly the scenario a per-message row-creation rule gets wrong.
/// <see cref="RecordEligibleAsync"/> therefore takes every message this page's upserts
/// touched, created or not, and the per-<c>(account, message, kind)</c> unique constraint
/// this checks against is what makes evaluating the same message more than once harmless
/// rather than merely rare.
/// </para>
/// <para>
/// <b>Staged content is evaluated at staging time, not at replay.</b> §3 states this
/// explicitly: "otherwise live mail would go unnotified for the entire backfill... worst on a
/// triggered resync of an established mailbox." <see cref="RecordEligibleFromStagedAsync"/>
/// records eligibility straight from the provider's own <see cref="MessageDto"/> — there is no
/// canonical row yet, so the record is keyed by <see cref="MessageDto.ProviderStableId"/>
/// instead of a message id, and <see cref="BackfillMessageIdsAsync"/> links it up once replay
/// finally materialises the row. A notification activated before that link exists still has
/// enough — title, body, provider id — to be shown; only click-through navigation needs the
/// local id, and by the doc's own words that case "fetches the message on demand rather than
/// the navigation failing" rather than being blocked on replay.
/// </para>
/// <para>
/// Two guards, because a single per-account provider baseline does not exist across
/// providers: <paramref name="notificationBaseline"/> (or its staged-path equivalent) is a
/// fixed instant this stream's own <see cref="ChangeStreamState.NotificationBaselineAt"/>
/// captured once, at stream creation or immediately on a triggered resync — <b>before</b> that
/// resync's own catch-up work runs, so mail arriving during the resync window is never
/// classified as predating it (§13 Epic 9: "advancing after would classify mail arriving
/// during the resync window as old and silently drop those notifications").
/// <see cref="Account.NotificationEpoch"/> is separate, account-wide, local policy: never
/// notify for anything dated before the account was set up, independent of which stream
/// happens to report it — this is what excludes a new account's initial backlog even though
/// staging establishes its stream's baseline on day one, before any backfill has run.
/// </para>
/// </remarks>
public sealed class NotificationService(MyloMailDbContext context, IHubEvents events, TimeProvider clock)
{
	/// <summary>
	/// Adds a tracked, not-yet-saved <see cref="NotificationRecord"/> for each eligible
	/// message not already notified, and returns the DTOs to announce once the caller commits.
	/// Callers add these to the same transaction as the message rows they describe — the
	/// record must never persist if the message it refers to did not (§9).
	/// </summary>
	public async Task<IReadOnlyList<NotificationDto>> RecordEligibleAsync(
		Account account,
		IReadOnlyList<Message> upserted,
		DateTimeOffset notificationBaseline,
		CancellationToken ct = default
	)
	{
		var candidates = Eligible(account, upserted, m => m.ReceivedAt, notificationBaseline);
		if (candidates.Count == 0)
		{
			return [];
		}

		var candidateIds = candidates.Select(m => m.Id).ToList();
		var alreadyByMessageId = await context
			.NotificationRecords.Where(n =>
				n.AccountId == account.Id
				&& n.Kind == NotificationKind.NewMessage
				&& n.MessageId != null
				&& candidateIds.Contains(n.MessageId!.Value)
			)
			.Select(n => n.MessageId!.Value)
			.ToListAsync(ct);
		var alreadyNotifiedIds = alreadyByMessageId.ToHashSet();

		// A message this page finds already has a canonical row can equally already have a
		// *pending*, staged-path notification for it — recorded under its provider stable id
		// by whichever ingest path (ordinary sync, replay, or coverage/backfill) got there
		// first, independently of this one. Without this check, that race produces both a
		// second, duplicate announcement and a pending row `BackfillMessageIdsAsync` can never
		// reach again, because a MessageId-keyed record already exists for the same message.
		var candidateStableIds = candidates.Where(m => m.ProviderStableId is not null).Select(m => m.ProviderStableId!).ToList();
		var pendingByStableId = candidateStableIds.Count == 0
			? []
			: await context
				.NotificationRecords.Where(n =>
					n.AccountId == account.Id
					&& n.Kind == NotificationKind.NewMessage
					&& n.MessageId == null
					&& n.ProviderStableId != null
					&& candidateStableIds.Contains(n.ProviderStableId!)
				)
				.ToListAsync(ct);
		var pendingByStableIdLookup = pendingByStableId.ToDictionary(n => n.ProviderStableId!);

		var announcements = new List<NotificationDto>();
		foreach (var message in candidates)
		{
			if (alreadyNotifiedIds.Contains(message.Id))
			{
				continue;
			}

			if (
				message.ProviderStableId is not null
				&& pendingByStableIdLookup.TryGetValue(message.ProviderStableId, out var pending)
			)
			{
				// Already announced when it was staged; link it up rather than notifying again.
				pending.MessageId = message.Id;
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
			announcements.Add(ToDto(record, Sender(message.From), message.Subject));
		}

		return announcements;
	}

	/// <summary>
	/// The staged-path equivalent of <see cref="RecordEligibleAsync"/>: evaluated against the
	/// provider's own DTOs before canonical replay has run, and keyed by
	/// <see cref="MessageDto.ProviderStableId"/> since no local message id exists yet.
	/// </summary>
	public async Task<IReadOnlyList<NotificationDto>> RecordEligibleFromStagedAsync(
		Account account,
		IReadOnlyList<MessageDto> upserted,
		DateTimeOffset notificationBaseline,
		CancellationToken ct = default
	)
	{
		var candidates = Eligible(account, upserted, m => m.ReceivedAt, notificationBaseline)
			.Where(m => m.ProviderStableId is not null)
			.ToList();
		if (candidates.Count == 0)
		{
			return [];
		}

		var candidateIds = candidates.Select(m => m.ProviderStableId!).ToList();
		var alreadyNotified = await context
			.NotificationRecords.Where(n =>
				n.AccountId == account.Id
				&& n.Kind == NotificationKind.NewMessage
				&& n.ProviderStableId != null
				&& candidateIds.Contains(n.ProviderStableId!)
			)
			.Select(n => n.ProviderStableId!)
			.ToListAsync(ct);
		var alreadyNotifiedIds = alreadyNotified.ToHashSet();

		var announcements = new List<NotificationDto>();
		foreach (var message in candidates)
		{
			if (alreadyNotifiedIds.Contains(message.ProviderStableId!))
			{
				continue;
			}

			var record = new NotificationRecord
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				ProviderStableId = message.ProviderStableId,
				Kind = NotificationKind.NewMessage,
				CreatedAt = clock.GetUtcNow(),
			};
			context.NotificationRecords.Add(record);
			announcements.Add(ToDto(record, Sender(message.From), message.Subject));
		}

		return announcements;
	}

	/// <summary>
	/// Links a staged-path notification to the local message replay just materialised, keyed
	/// by the provider's stable id both sides share. Runs inside replay's own transaction, so
	/// a crash either links both the message and the notification or neither.
	/// </summary>
	public async Task BackfillMessageIdsAsync(
		Guid accountId,
		IReadOnlyDictionary<string, Guid> resolvedByProviderStableId,
		CancellationToken ct = default
	)
	{
		if (resolvedByProviderStableId.Count == 0)
		{
			return;
		}

		var stableIds = resolvedByProviderStableId.Keys.ToList();
		var pending = await context
			.NotificationRecords.Where(n =>
				n.AccountId == accountId && n.MessageId == null && n.ProviderStableId != null && stableIds.Contains(n.ProviderStableId!)
			)
			.ToListAsync(ct);

		foreach (var record in pending)
		{
			if (resolvedByProviderStableId.TryGetValue(record.ProviderStableId!, out var messageId))
			{
				record.MessageId = messageId;
			}
		}
	}

	/// <summary>Announces DTOs a caller already recorded, once its transaction has committed.</summary>
	public async Task AnnounceAsync(IReadOnlyList<NotificationDto> notifications)
	{
		foreach (var notification in notifications)
		{
			await events.NotificationReadyAsync(notification);
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
			.ToListAsync(ct);
		if (pending.Count == 0)
		{
			return;
		}

		var messageIds = pending.Where(n => n.MessageId is not null).Select(n => n.MessageId!.Value).ToList();
		var messages = await context.Messages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

		foreach (var record in pending)
		{
			// A pending record whose message has not materialised yet is redispatched with no
			// content preview rather than skipped — silently dropping it is the one outcome
			// this policy forbids, and the client already has to handle activating a
			// notification before its message exists (§3).
			var dto = record.MessageId is Guid messageId && messages.TryGetValue(messageId, out var message)
				? ToDto(record, Sender(message.From), message.Subject)
				: new NotificationDto(record.Id, record.AccountId, record.MessageId, "New message", string.Empty);

			await events.NotificationReadyAsync(dto);
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

	private static List<T> Eligible<T>(
		Account account,
		IReadOnlyList<T> upserted,
		Func<T, DateTimeOffset> receivedAt,
		DateTimeOffset notificationBaseline
	)
	{
		if (!account.NotificationsEnabled || upserted.Count == 0)
		{
			return [];
		}

		var cutoff = account.NotificationEpoch > notificationBaseline ? account.NotificationEpoch : notificationBaseline;
		return upserted.Where(m => receivedAt(m) >= cutoff).ToList();
	}

	private static NotificationDto ToDto(NotificationRecord record, string sender, string subject) =>
		new(record.Id, record.AccountId, record.MessageId, sender, subject);

	private static string Sender(IReadOnlyList<Address> from) =>
		from.Count > 0 ? from[0].Name ?? from[0].Email : "New message";
}
