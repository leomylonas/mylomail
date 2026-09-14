using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Outbox;

/// <summary>
/// Queueing, cancelling and claiming outbox items (§15).
/// </summary>
/// <remarks>
/// Every status transition here is a compare-and-swap. Cancel and the send worker race for
/// the same item by design, and exactly one must win: a check followed by a write leaves a
/// window in which both observe <see cref="OutboxStatus.Scheduled"/> and both proceed — one
/// cancelling a message the other is already sending.
/// </remarks>
public sealed class OutboxService(
	MyloMailDbContext context,
	TimeProvider clock,
	IHubEvents events,
	IOutboxDispatcher dispatcher,
	IMailProviderFactory providers
)
{
	private const string TransitionSql = """
		UPDATE "OutboxItems"
		SET "Status" = $to
		WHERE "Id" = $id AND "Status" IN ($from1, $from2);
		""";

	/// <summary>
	/// Queues a draft for sending after the account's undo-send delay, or at an explicit time.
	/// </summary>
	/// <remarks>
	/// The stable <c>Message-ID</c> is generated here, before any attempt exists, because a
	/// send that crashes before its first observable result still has to be identifiable in
	/// the Sent mailbox afterwards.
	/// </remarks>
	public async Task<OutboxItem> QueueAsync(
		Account account,
		Guid draftId,
		DateTimeOffset? scheduledFor = null,
		CancellationToken ct = default
	)
	{
		var draft = await context.Drafts.SingleAsync(
			candidate => candidate.Id == draftId && candidate.AccountId == account.Id,
			ct
		);
		var now = clock.GetUtcNow();
		var assignedStableIdentity = draft.StableMessageId is null;
		var stableMessageId = draft.StableMessageId ?? NewMessageId(account);
		draft.StableMessageId = stableMessageId;
		if (
			assignedStableIdentity
			&& account.ProviderType == ProviderType.Microsoft365
			&& draft.ProviderDraftId is not null
		)
		{
			// An older/materialised Graph draft may not carry the local reconciliation
			// identity on the server. Force the pre-send draft push to persist it before the
			// externally visible send attempt is claimed.
			draft.PushedAt = null;
		}

		var item = await context.OutboxItems.FirstOrDefaultAsync(
			candidate => candidate.StableMessageId == stableMessageId,
			ct
		);
		if (item?.Status == OutboxStatus.AmbiguousOutcome)
		{
			if (
				item.ReconcilingSince is not { } reconcilingSince
				|| now - reconcilingSince < SendReconciler.ReconciliationWindow
			)
			{
				throw new InvalidOperationException(
					"This send is still being reconciled and cannot be sent again yet."
				);
			}

			if (
				account.ProviderType == ProviderType.Microsoft365
				&& (draft.ProviderMessageId ?? draft.ProviderDraftId) is { } providerMessageId
			)
			{
				// The immutable id may now name the already-sent item rather than a mutable
				// draft. Resolve that provider fact before authorising a new send.
				var remoteDraft = await providers
					.For(account)
					.FindDraftByMessageIdAsync(account, providerMessageId, ct);
				if (remoteDraft is null)
				{
					draft.ProviderDraftId = null;
					draft.ProviderMessageId = null;
					draft.PushedAt = null;
					draft.ProviderRevision = null;
				}
				else
				{
					draft.ProviderDraftId = remoteDraft.ProviderDraftId;
					draft.ProviderMessageId =
						remoteDraft.ProviderMessageId ?? remoteDraft.ProviderDraftId;
					draft.ProviderRevision = remoteDraft.ProviderRevision;
				}
			}

			// Reaching QueueAsync again is the user's explicit send-again action after the
			// ambiguity window. Reuse the durable row, but mint a new Message-ID so the new
			// attempt cannot be mistaken for the first possibly-successful send.
			stableMessageId = NewMessageId(account);
			draft.StableMessageId = stableMessageId;
			item.StableMessageId = stableMessageId;
			if (
				account.ProviderType == ProviderType.Microsoft365
				&& draft.ProviderDraftId is not null
			)
			{
				draft.PushedAt = null;
			}
			await context
				.MutationExecutionAttempts.Where(attempt =>
					attempt.OutboxItemId == item.Id && attempt.State != MutationAttemptState.Completed
				)
				.ExecuteUpdateAsync(
					setters =>
						setters
							.SetProperty(attempt => attempt.State, MutationAttemptState.Completed)
							.SetProperty(attempt => attempt.ResultPersistedAt, now),
					ct
				);
		}
		else if (
			item is not null
			&& item.Status is not OutboxStatus.Cancelled
			&& item.Status is not OutboxStatus.Failed
		)
		{
			throw new InvalidOperationException("This draft is already queued for sending.");
		}
		if (item is null)
		{
			item = new OutboxItem
			{
				Id = Guid.NewGuid(),
				AccountId = account.Id,
				DraftId = draftId,
				StableMessageId = stableMessageId,
			};
			context.OutboxItems.Add(item);
		}
		item.DraftId = draftId;
		item.Status = OutboxStatus.Scheduled;
		item.ScheduledSendAt = scheduledFor ?? now.AddSeconds(account.UndoSendDelaySeconds);
		item.CreatedAt = now;
		item.Attempts = 0;
		item.RecipientSnapshot = [];
		item.LastError = null;
		item.SentAt = null;
		item.ReconcilingSince = null;
		await context.SaveChangesAsync(ct);

		// The outbox is the one place a user watches a thing they cannot cancel much longer,
		// so every transition is reported (§7, §15).
		await AnnounceAsync(item.Id, ct);

		// Asked for after the commit, and scheduled for when the undo window closes rather
		// than now: dispatching immediately would send the message the user still believes
		// they can stop. Without this the item waits for the next startup sweep — queued,
		// durable, and never sent.
		dispatcher.RequestSend(item.AccountId, item.ScheduledSendAt - now);

		return item;
	}

	/// <summary>
	/// Attempts to cancel. Returns false once the worker has taken the item.
	/// </summary>
	/// <remarks>
	/// A refusal is final: once <see cref="OutboxStatus.Sending"/>, the message may already
	/// be on its way and reverting to a draft would misrepresent that. The UI reports "too
	/// late" rather than pretending.
	/// </remarks>
	public async Task<bool> TryCancelAsync(Guid outboxItemId, CancellationToken ct = default) =>
		await TransitionAsync(
			outboxItemId,
			OutboxStatus.Cancelled,
			OutboxStatus.Scheduled,
			OutboxStatus.Pending,
			ct
		);

	/// <summary>Attempts to take the item for sending. Returns false if cancellation won.</summary>
	public async Task<bool> TryClaimForSendAsync(Guid outboxItemId, CancellationToken ct = default) =>
		await TransitionAsync(
			outboxItemId,
			OutboxStatus.Sending,
			OutboxStatus.Scheduled,
			OutboxStatus.Pending,
			ct
		);

	/// <summary>Items whose time has come and which nothing else has taken.</summary>
	public async Task<IReadOnlyList<OutboxItem>> DueAsync(Guid accountId, CancellationToken ct = default)
	{
		var now = clock.GetUtcNow();
		var candidates = await context
			.OutboxItems.Where(o =>
				o.AccountId == accountId
				&& (o.Status == OutboxStatus.Scheduled || o.Status == OutboxStatus.Pending)
			)
			.AsNoTracking()
			.ToListAsync(ct);

		// Filtered here rather than in SQL: SQLite cannot compare DateTimeOffset usefully,
		// and this is a small set by construction.
		return [.. candidates.Where(o => o.ScheduledSendAt <= now).OrderBy(o => o.ScheduledSendAt)];
	}

	private async Task<bool> TransitionAsync(
		Guid id,
		OutboxStatus to,
		OutboxStatus from1,
		OutboxStatus from2,
		CancellationToken ct
	)
	{
		var rows = await context.Database.ExecuteSqlRawAsync(
			TransitionSql,
			[
				new SqliteParameter("$id", id),
				new SqliteParameter("$to", (int)to),
				new SqliteParameter("$from1", (int)from1),
				new SqliteParameter("$from2", (int)from2),
			],
			ct
		);

		context.ChangeTracker.Clear();

		if (rows == 1)
		{
			await AnnounceAsync(id, ct);
		}

		return rows == 1;
	}

	/// <summary>
	/// Fires <see cref="IHubEvents.OutboxStatusChangedAsync"/> for a status write made outside
	/// this service's own CAS transitions — used by <see cref="SendExecutor"/> when it fails an
	/// item directly (a vanished draft, or one with an unresolved <see cref="Draft.SyncConflict"/>)
	/// rather than racing cancellation for it. Every <see cref="OutboxItem.Status"/> transition
	/// is announced (§7); a write that skips this leaves the renderer showing a stale status
	/// with no explanation until the next full resync.
	/// </summary>
	public Task AnnounceStatusAsync(Guid outboxItemId, CancellationToken ct = default) =>
		AnnounceAsync(outboxItemId, ct);

	private async Task AnnounceAsync(Guid outboxItemId, CancellationToken ct)
	{
		var item = await context.OutboxItems.AsNoTracking().FirstOrDefaultAsync(o => o.Id == outboxItemId, ct);
		if (item is not null)
		{
			await events.OutboxStatusChangedAsync(
				new OutboxItemDto(
					item.Id,
					item.AccountId,
					item.Status,
					item.ScheduledSendAt,
					item.LastError,
					item.ReconcilingSince
				)
			);
		}
	}

	/// <summary>
	/// An RFC 5322 message identifier, using the account's own domain so it is plausible to
	/// receiving servers and to the provider's own Sent copy.
	/// </summary>
	private static string NewMessageId(Account account)
	{
		var domain = account.ProviderType switch
		{
			ProviderType.Gmail => "mail.gmail.com",
			ProviderType.Microsoft365 => "outlook.com",
			_ => "mylomail.local",
		};

		return $"<{Guid.NewGuid():N}@{domain}>";
	}
}
