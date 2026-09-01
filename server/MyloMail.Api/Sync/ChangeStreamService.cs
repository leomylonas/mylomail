using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// Live change tracking (§3). The scope varies by provider: one account-wide stream for
/// Gmail, one per mailbox for Graph and IMAP.
/// </summary>
/// <remarks>
/// <b>The governing rule is that a cursor is never persisted past changes that have not been
/// durably persisted.</b> The cursor and the state it represents commit in the same
/// transaction, always. Replaying a page is acceptable — every write is an upsert. Skipping
/// one is not, and it is silent.
/// </remarks>
public sealed class ChangeStreamService(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	MessageIngestor ingestor,
	RemoteDraftMaterializer drafts,
	TimeProvider clock,
	IFaultInjector faults,
	IHubEvents events,
	Notifications.NotificationService notifications,
	ILogger<ChangeStreamService> logger
)
{
	/// <summary>
	/// Walks the change stream for one mailbox until the provider stops offering
	/// continuations.
	/// </summary>
	/// <exception cref="ProviderCursorInvalidException">
	/// Never propagates: an invalid cursor from any provider takes the same triggered
	/// resynchronisation path, handled once here rather than three times at the call sites.
	/// </exception>
	public async Task<ChangeStreamOutcome> SyncAsync(Account account, Mailbox mailbox, CancellationToken ct = default)
	{
		var provider = providers.For(account);
		var state = await GetOrCreateStateAsync(account, mailbox, provider.Capabilities, ct);

		// While coverage is incomplete, Gmail's account-wide history is drained durably but
		// left unapplied. Applying it concurrently with backfill lets a stale backfill page
		// resurrect a membership history has already removed.
		var stage = provider.Capabilities.ChangeStreamScope == ChangeStreamScope.Account
			&& !await CoverageCompleteAsync(account, ct);

		string? continuation = null;
		var pages = 0;

		try
		{
			while (true)
			{
				// Captured before the call, so it records the topology the page was issued
				// against rather than whatever topology exists once it returns.
				var generations = GenerationSnapshot.Capture(
					(await MailboxesByProviderIdAsync(account, ct)).Values
				);

				var result = await provider.SyncMailboxAsync(account, mailbox, state.CursorState, continuation, ct);

				faults.Reached(FaultPoints.SyncPageBeforeCommit);

				var remoteDrafts = await drafts.PrepareAsync(account, result.Upserted, await MailboxesByProviderIdAsync(account, ct), ct);

				if (stage)
				{
					await StagePageAsync(account, state, result, remoteDrafts, generations, ct);
				}
				else
				{
					await ApplyPageAsync(account, state, result, remoteDrafts, generations, ct);
				}

				faults.Reached(FaultPoints.SyncPageAfterCommit);

				pages++;
				continuation = result.Continuation;

				if (!result.HasMore)
				{
					break;
				}
			}
		}
		catch (ProviderCursorInvalidException ex)
		{
			await TriggerResynchronisationAsync(account, mailbox, state, ex, ct);
			return new ChangeStreamOutcome(pages, Staged: stage, ResyncTriggered: true);
		}

		await AnnounceMailboxAsync(mailbox, ct);
		return new ChangeStreamOutcome(pages, stage, ResyncTriggered: false);
	}

	/// <summary>
	/// Reports the mailbox's counts at the end of a run.
	/// </summary>
	/// <remarks>
	/// Both numbers are sent, because they answer different questions: the provider's count is
	/// what the sidebar shows, and the local one is only correct for "how much is held here"
	/// (§1). Sending one and letting the UI guess would make bounded sync look like data loss.
	/// </remarks>
	private async Task AnnounceMailboxAsync(Mailbox mailbox, CancellationToken ct)
	{
		var current = await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == mailbox.Id, ct);
		if (current is null)
		{
			return;
		}

		var localCount = await context.MessageMailboxes.CountAsync(o => o.MailboxId == mailbox.Id, ct);
		var coverage = await context
			.MailboxCoverageStates.Where(c => c.MailboxId == mailbox.Id)
			.Select(c => (CoverageStatus?)c.Status)
			.FirstOrDefaultAsync(ct);

		await events.MailboxUpdatedAsync(
			new MailboxSummaryDto(
				current.Id,
				current.AccountId,
				current.ParentId,
				current.Name,
				current.SpecialUse,
				current.ProviderTotalCount,
				current.ProviderUnreadCount,
				localCount,
				coverage ?? CoverageStatus.NotStarted
			)
		);
	}

	/// <summary>
	/// Applies a page and advances the cursor in one transaction.
	/// </summary>
	/// <remarks>
	/// The cursor is only advanced when the provider offers one. A null
	/// <see cref="SyncResult.NewCursor"/> means it is not yet safe to commit — mid-walk,
	/// Gmail reports a <c>historyId</c> for the whole list and Graph yields a
	/// <c>nextLink</c> rather than the <c>deltaLink</c> incremental sync needs — so advancing
	/// here would hand the next run a cursor covering changes it never received.
	/// </remarks>
	private async Task ApplyPageAsync(
		Account account,
		ChangeStreamState state,
		SyncResult result,
		IReadOnlyList<RemoteDraftPayload> remoteDrafts,
		GenerationSnapshot generations,
		CancellationToken ct
	)
	{
		var mailboxes = await MailboxesByProviderIdAsync(account, ct);
		ContentApplyResult applied = new(new IngestResult([], [], []), [], []);

		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			applied = await ApplyContentAsync(
				account,
				result,
				remoteDrafts,
				mailboxes,
				generations,
				state.NotificationBaselineAt,
				ct
			);

			if (result.NewCursor is not null)
			{
				state.CursorState = result.NewCursor;
				state.CursorKind = result.NewCursor.Kind;
			}

			state.LastSyncedAt = clock.GetUtcNow();
			state.BaselineEstablishedAt ??= clock.GetUtcNow();
			state.LastError = null;

			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});

		// After the commit, never before: an event announcing mail that a crash then discarded
		// would leave the UI showing something the database does not have.
		//
		// These are steady-state changes by construction — this is the change stream, not
		// backfill — so a new message here is genuinely new mail (§7).
		var summaries = applied.Messages.Observed.ToDictionary(m => m.Id, MessageEventMapper.ToSummary);

		foreach (var message in applied.Messages.Created)
		{
			await events.MessageReceivedAsync(summaries[message.Id]);
		}

		foreach (var message in applied.Messages.Updated)
		{
			await events.MessageUpdatedAsync(summaries[message.Id]);
		}

		await notifications.AnnounceAsync(applied.EligibleNotifications);

		foreach (var draftId in applied.DraftIds)
		{
			await events.DraftUpdatedAsync(draftId);
		}
	}

	private async Task<ContentApplyResult> ApplyContentAsync(
		Account account,
		SyncResult result,
		IReadOnlyList<RemoteDraftPayload> remoteDrafts,
		Dictionary<string, Mailbox> mailboxes,
		GenerationSnapshot generations,
		DateTimeOffset? notificationBaseline,
		CancellationToken ct
	)
	{
		var ingested = await ingestor.IngestAsync(account, result.Upserted, mailboxes, generations, ct);

		// Links a staged-path notification (recorded before this row existed) to it now that
		// canonical ingest has resolved it, regardless of which path resolved it first (§3).
		var resolvedByProviderStableId = ingested
			.Observed.Where(m => m.ProviderStableId is not null)
			.ToDictionary(m => m.ProviderStableId!, m => m.Id);
		await notifications.BackfillMessageIdsAsync(account.Id, resolvedByProviderStableId, ct);

		// Null specifically means "this page was already evaluated while staged" (§3) —
		// replay only links the notification recorded then to the row just resolved above,
		// via the backfill; evaluating again here from row creation would double-notify,
		// once under the provider's stable id and once under the now-known message id, since
		// the two dedup on different keys.
		//
		// Otherwise, eligibility comes from this page reporting the message via the change
		// stream, not from whether the ingestor happened to find an existing row: a message
		// backfill already materialised is exactly the case §13 Epic 9 requires this to still
		// catch. The durable per-(account, message, kind) record makes re-evaluating a
		// message the stream reports again on some later page harmless rather than merely
		// rare.
		var eligibleNotifications = notificationBaseline is { } baseline
			? await notifications.RecordEligibleAsync(account, ingested.Observed, baseline, ct)
			: [];

		var draftIds = new List<Guid>(
			await drafts.ApplyAsync(account, remoteDrafts, mailboxes, generations, ct)
		);
		var changed = new List<Domain.Message>(ingested.Updated);

		foreach (var change in result.FlagChanges)
		{
			if (mailboxes.TryGetValue(change.ProviderMailboxId, out var target))
			{
				var message = await ingestor.ApplyFlagChangeAsync(target, change, generations, ct);
				if (message is not null)
				{
					changed.Add(message);
				}
			}
		}

		foreach (var removal in result.Removed)
		{
			if (mailboxes.TryGetValue(removal.ProviderMailboxId, out var target))
			{
				// Removes the occurrence, never the canonical message: a Graph move surfaces
				// as a removal and an addition in either order.
				await ingestor.RemoveOccurrenceAsync(target, removal.ProviderOccurrenceId, generations, ct);
			}
		}

		draftIds.AddRange(
			await drafts.ApplyRemovalsAsync(account, result.Removed, mailboxes, generations, ct)
		);

		return new ContentApplyResult(
			new IngestResult(ingested.Created, changed, ingested.Observed),
			draftIds.Distinct().ToArray(),
			eligibleNotifications
		);
	}

	/// <summary>
	/// Stages a page durably without applying it, advancing the cursor in the same
	/// transaction. The cursor may advance here precisely because the page <i>is</i> durably
	/// persisted — staged rather than applied is still persisted.
	/// </summary>
	private async Task StagePageAsync(
		Account account,
		ChangeStreamState state,
		SyncResult result,
		IReadOnlyList<RemoteDraftPayload> remoteDrafts,
		GenerationSnapshot generations,
		CancellationToken ct
	)
	{
		IReadOnlyList<NotificationDto> eligibleNotifications = [];

		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			var highest = await context
				.StagedChangeEvents.Where(s => s.AccountId == account.Id)
				.MaxAsync(s => (long?)s.Ordinal, ct);

			context.StagedChangeEvents.Add(
				new StagedChangeEvent
				{
					Id = Guid.NewGuid(),
					AccountId = account.Id,
					Ordinal = (highest ?? 0) + 1,
					// The generations are staged with the page. Replay may be hours later, and
					// the check has to be against the topology the page was observed under,
					// not the topology that exists when it is finally applied.
					Payload = SyncPagePayload.Serialize(result, remoteDrafts, generations),
					StagedAt = clock.GetUtcNow(),
				}
			);

			if (result.NewCursor is not null)
			{
				state.CursorState = result.NewCursor;
				state.CursorKind = result.NewCursor.Kind;
			}

			state.LastSyncedAt = clock.GetUtcNow();
			state.BaselineEstablishedAt ??= clock.GetUtcNow();

			// Evaluated now, from the provider's own DTOs, rather than deferred behind
			// canonical replay — otherwise live mail goes unnotified for the length of the
			// backfill this page is staged behind, worst on a resync of an established
			// mailbox (§3). There is no local message row yet, so this is keyed by the
			// provider's own stable id and linked up once replay resolves it.
			eligibleNotifications = await notifications.RecordEligibleFromStagedAsync(
				account,
				result.Upserted,
				state.NotificationBaselineAt,
				ct
			);

			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});

		await notifications.AnnounceAsync(eligibleNotifications);
	}

	/// <summary>
	/// Replays staged events into the canonical model, in observation order, once coverage is
	/// complete.
	/// </summary>
	/// <remarks>
	/// Each event is applied and deleted in one transaction, so a crash mid-replay resumes
	/// from the first event that has not been applied rather than reapplying the whole queue
	/// or skipping part of it.
	/// </remarks>
	public async Task<int> ReplayStagedAsync(Account account, CancellationToken ct = default)
	{
		faults.Reached(FaultPoints.SyncBeforeStagedReplay);

		var mailboxes = await MailboxesByProviderIdAsync(account, ct);
		var replayed = 0;

		while (true)
		{
			var staged = await context
				.StagedChangeEvents.Where(s => s.AccountId == account.Id)
				.OrderBy(s => s.Ordinal)
				.FirstOrDefaultAsync(ct);

			if (staged is null)
			{
				break;
			}

			var (result, remoteDrafts, generations) = SyncPagePayload.Deserialize(staged.Payload);
			ContentApplyResult applied = new(new IngestResult([], [], []), [], []);

			var strategy = context.Database.CreateExecutionStrategy();
			await strategy.ExecuteAsync(async () =>
			{
				await using var transaction = await context.Database.BeginTransactionAsync(ct);

				// Eligibility for this page was already evaluated and announced while it was
				// staged (§3); replay's own notification work is only the backfill inside
				// ApplyContentAsync that links those records to the row it resolves here.
				applied = await ApplyContentAsync(
					account,
					result,
					remoteDrafts,
					mailboxes,
					generations,
					notificationBaseline: null,
					ct
				);
				context.StagedChangeEvents.Remove(staged);

				await context.SaveChangesAsync(ct);
				await transaction.CommitAsync(ct);

			});

			// These are steady-state changes too — replayed live-stream history, not
			// backfill — so a message reported here is genuinely new mail just as it is on
			// the non-staged path (§7).
			var summaries = applied.Messages.Observed.ToDictionary(m => m.Id, MessageEventMapper.ToSummary);

			foreach (var message in applied.Messages.Created)
			{
				await events.MessageReceivedAsync(summaries[message.Id]);
			}

			foreach (var message in applied.Messages.Updated)
			{
				await events.MessageUpdatedAsync(summaries[message.Id]);
			}

			foreach (var draftId in applied.DraftIds)
			{
				await events.DraftUpdatedAsync(draftId);
			}

			replayed++;
		}

		logger.LogInformation("Replayed {Count} staged change pages for account {AccountId}.", replayed, account.Id);
		return replayed;
	}

	/// <summary>
	/// Triggered resynchronisation: something is wrong. The cursor is discarded and a baseline
	/// re-established.
	/// </summary>
	/// <remarks>
	/// This is deliberately the same machinery as periodic integrity reconciliation but a
	/// different trigger, cadence and meaning. Every provider can invalidate a cursor —
	/// IMAP's <c>UIDVALIDITY</c> changing, Gmail's <c>historyId</c> expiring, Graph returning
	/// <c>410 Gone</c> — and all three need this identical response, which is why it is
	/// handled once.
	/// </remarks>
	private async Task TriggerResynchronisationAsync(
		Account account,
		Mailbox mailbox,
		ChangeStreamState state,
		ProviderCursorInvalidException ex,
		CancellationToken ct
	)
	{
		state.CursorState = null;
		state.BaselineEstablishedAt = null;
		state.LastError = ex.Message;

		// Captured here, before resynchronisation begins, never advanced after it completes —
		// advancing it afterwards would classify mail that arrived during the resync window
		// as predating it and silently drop those notifications (§13 Epic 9).
		state.NotificationBaselineAt = clock.GetUtcNow();

		var coverage = await context.MailboxCoverageStates.FirstOrDefaultAsync(c => c.MailboxId == mailbox.Id, ct);
		if (coverage is not null)
		{
			// The bound is re-applied from the start: a baseline is what is being
			// re-established, not a continuation of the old one.
			coverage.Status = CoverageStatus.NotStarted;
			coverage.ResumeToken = null;
		}

		var integrity = await context.IntegrityReconciliationStates.FirstOrDefaultAsync(
			i => i.MailboxId == mailbox.Id,
			ct
		);
		if (integrity is null)
		{
			integrity = new IntegrityReconciliationState { MailboxId = mailbox.Id };
			context.IntegrityReconciliationStates.Add(integrity);
		}

		integrity.LastError = ex.Message;

		await context.SaveChangesAsync(ct);

		logger.LogWarning(
			"Cursor for mailbox {MailboxId} on account {AccountId} was invalidated; baseline will be re-established.",
			mailbox.Id,
			account.Id
		);
	}

	/// <summary>
	/// Gmail's stream is account-scoped and its state row therefore has a null mailbox.
	/// Per-label cursors would be fiction, would consume the same stream repeatedly, and would
	/// race between label jobs (§1).
	/// </summary>
	private async Task<ChangeStreamState> GetOrCreateStateAsync(
		Account account,
		Mailbox mailbox,
		ProviderCapabilities capabilities,
		CancellationToken ct
	)
	{
		Guid? scope = capabilities.ChangeStreamScope == ChangeStreamScope.Account ? null : mailbox.Id;

		var state = await context.ChangeStreamStates.FirstOrDefaultAsync(
			s => s.AccountId == account.Id && s.MailboxId == scope,
			ct
		);

		if (state is not null)
		{
			return state;
		}

		state = new ChangeStreamState
		{
			Id = Guid.NewGuid(),
			AccountId = account.Id,
			MailboxId = scope,
			CursorKind = capabilities.Type switch
			{
				ProviderType.Gmail => CursorKind.GmailHistory,
				ProviderType.Microsoft365 => CursorKind.GraphDelta,
				_ => CursorKind.ImapUid,
			},
			// Captured now, before this stream's first page ever runs — not after it, which
			// would let that first page's catch-up notify for whatever it happens to find.
			NotificationBaselineAt = clock.GetUtcNow(),
		};

		context.ChangeStreamStates.Add(state);
		await context.SaveChangesAsync(ct);
		return state;
	}

	/// <summary>
	/// Coverage is complete only when every mailbox in the account has reached
	/// <see cref="CoverageStatus.Covered"/>.
	/// </summary>
	/// <remarks>
	/// A mailbox with no coverage row has not started, which is emphatically not the same as
	/// having finished. Asking only whether any existing row is incomplete would call a
	/// freshly discovered account fully covered and start applying history to a canonical
	/// model that has nothing in it yet.
	/// </remarks>
	private async Task<bool> CoverageCompleteAsync(Account account, CancellationToken ct) =>
		!await context
			.Mailboxes.Where(m => m.AccountId == account.Id)
			.AnyAsync(
				m =>
					!context.MailboxCoverageStates.Any(c =>
						c.MailboxId == m.Id && c.Status == CoverageStatus.Covered
					),
				ct
			);

	private async Task<Dictionary<string, Mailbox>> MailboxesByProviderIdAsync(Account account, CancellationToken ct) =>
		await context
			.Mailboxes.Where(m => m.AccountId == account.Id && m.ProviderMailboxId != null)
			// Read untracked, so the generation compared against is the one currently in the
			// database rather than a value this context happened to load before the page was
			// issued. Only occurrence writes follow, and those carry the mailbox id by value.
			.AsNoTracking()
			.ToDictionaryAsync(m => m.ProviderMailboxId!, m => m, ct);
}

/// <summary>What one change-stream run did, for the caller's scheduling decision.</summary>
public sealed record ChangeStreamOutcome(int Pages, bool Staged, bool ResyncTriggered);

internal sealed record ContentApplyResult(
	IngestResult Messages,
	IReadOnlyList<Guid> DraftIds,
	IReadOnlyList<NotificationDto> EligibleNotifications
);
