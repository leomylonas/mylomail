using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Compose;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

namespace MyloMail.Api.Sync;

/// <summary>
/// Historical backfill: how much of the user's requested history has been materialised
/// locally (§1, §3).
/// </summary>
/// <remarks>
/// <b>Bounds are coverage targets, not membership limits.</b> "Last N messages" means
/// actively enumerate at least the newest N for that mailbox; messages discovered through
/// another mailbox or the account change stream may also appear there — necessarily so under
/// Gmail's label model, where one message belongs to several mailboxes at once.
/// </remarks>
public sealed class CoverageService(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	MessageIngestor ingestor,
	RemoteDraftMaterializer drafts,
	TimeProvider clock,
	IFaultInjector faults,
	IHubEvents events,
	Notifications.NotificationService notifications,
	ILogger<CoverageService> logger
)
{
	/// <summary>
	/// Fetches one page and commits it together with the resume token that covers it.
	/// </summary>
	/// <remarks>
	/// The page and its token commit in the same transaction, always. Replaying a page is
	/// acceptable — every write is an upsert — whereas skipping one is silent and permanent.
	/// </remarks>
	/// <returns>True while more pages remain.</returns>
	public async Task<bool> RunPageAsync(Account account, Mailbox mailbox, int pageSize = 200, CancellationToken ct = default)
	{
		var coverage = await GetOrCreateAsync(mailbox, ct);
		if (coverage.Status == CoverageStatus.Covered)
		{
			return false;
		}

		var policyGeneration = mailbox.CoveragePolicyGeneration;

		if (account.ProviderType == ProviderType.Gmail
			&& !await context.ChangeStreamStates.AnyAsync(
				state => state.AccountId == account.Id
					&& state.MailboxId == null
					&& state.CursorState != null
					&& !state.IsRebasing,
				ct
			))
		{
			throw new CoverageBaselinePendingException();
		}

		var mode = mailbox.InitialSyncModeOverride ?? account.InitialSyncMode;
		var bound = mailbox.InitialSyncBoundValueOverride ?? account.InitialSyncBoundValue;

		var availabilityChanged = coverage.Status != CoverageStatus.Backfilling;
		coverage.Status = CoverageStatus.Backfilling;
		coverage.StartedAt ??= clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
		if (availabilityChanged)
		{
			await MailboxSummaryDtoFactory.AnnounceAsync(context, events, account.Id, mailbox.Id, ct);
		}

		// Captured before the call: this records the incarnation the page is being fetched
		// for, which is the only thing it is valid to write against.
		var generations = GenerationSnapshot.Capture([mailbox]);

		var page = await providers
			.For(account)
			.InitialSyncMailboxAsync(account, mailbox, coverage.ResumeToken, mode, bound, pageSize, ct);

		faults.Reached(FaultPoints.SyncPageBeforeCommit);

		var mailboxes = await MailboxesByProviderIdAsync(account, ct);
		var remoteDrafts = await drafts.PrepareAsync(account, page.Messages, mailboxes, ct);
		IReadOnlyList<Guid> changedDraftIds = [];
		IReadOnlyList<Message> rethreaded = [];
		IReadOnlyList<Guid> countedMailboxIds = [];
		var contactSuggestionsChanged = false;

		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			if (!await context.Accounts.AnyAsync(a => a.Id == account.Id && a.IsEnabled, ct))
			{
				// Account removal starts by disabling the row. A provider call already in
				// flight may still return, but none of its observations may commit afterward.
				throw new CoverageBaselinePendingException();
			}

			await context.Entry(mailbox).ReloadAsync(ct);
			await context.Entry(coverage).ReloadAsync(ct);
			if (mailbox.CoveragePolicyGeneration != policyGeneration)
			{
				throw new CoverageBaselinePendingException();
			}
			if (account.ProviderType == ProviderType.Gmail
				&& !await context.ChangeStreamStates.AnyAsync(
					state => state.AccountId == account.Id
						&& state.MailboxId == null
						&& state.CursorState != null
						&& !state.IsRebasing,
					ct
				))
			{
				throw new CoverageBaselinePendingException();
			}
			if (!generations.StillCurrent(mailbox.ProviderMailboxId, mailbox))
			{
				throw new CoverageBaselinePendingException();
			}

			var ingested = await ingestor.IngestAsync(account, page.Messages, mailboxes, generations, ct);
			rethreaded = ingested.Rethreaded;
			countedMailboxIds = ingested.CountedMailboxIds;
			contactSuggestionsChanged = ingested.ContactSuggestionsChanged;
			changedDraftIds = await drafts.ApplyAsync(account, remoteDrafts, mailboxes, generations, ct);

			// A message this page materialises may already have a pending, staged-path
			// notification recorded under its provider stable id (§3) — backfill's own
			// ingestion never raises one itself, but it can race a still-unreplayed staged
			// arrival for the same message, and leaving that record unlinked here is exactly
			// the gap that let a later ordinary sync page insert a duplicate for it.
			var resolvedByProviderStableId = ingested
				.Created.Concat(ingested.Updated)
				.Where(m => m.ProviderStableId is not null)
				.ToDictionary(m => m.ProviderStableId!, m => m.Id);
			await notifications.BackfillMessageIdsAsync(account.Id, resolvedByProviderStableId, ct);

			// Backfill raises no new-message event: this is a backlog the user already has,
			// but persisted descendants rethreaded by this page still invalidate their lists.
			coverage.MessagesFetched += page.Messages.Count;
			coverage.EstimatedTotal = page.EstimatedTotal ?? coverage.EstimatedTotal;
			coverage.ResumeToken = page.ResumeToken;
			coverage.LastError = null;

			if (!page.HasMore)
			{
				coverage.Status = CoverageStatus.Covered;
			}

			faults.Reached(FaultPoints.SyncPageAfterApplyBeforeCommit);
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});

		faults.Reached(FaultPoints.SyncPageAfterCommit);

		// After the page is committed, never before: an event announcing progress that a crash
		// then discarded would leave the UI ahead of the database.
		await events.SyncProgressAsync(
			new SyncProgressDto(
				mailbox.Id,
				SyncProgressKind.Coverage,
				coverage.Status,
				coverage.MessagesFetched,
				coverage.EstimatedTotal
			)
		);
		await MailboxSummaryDtoFactory.AnnounceAsync(context, events, account.Id, mailbox.Id, ct);

		// A page fetched for one mailbox can still fill another: under Gmail's canonical
		// model one message carries several labels, so the mailbox being backfilled is not
		// the set of mailboxes whose counts this page moved (§7).
		await MailboxSummaryDtoFactory.AnnounceManyAsync(
			context,
			events,
			countedMailboxIds.Where(id => id != mailbox.Id),
			ct
		);
		foreach (var draftId in changedDraftIds)
		{
			await events.DraftUpdatedAsync(draftId);
		}
		foreach (var message in rethreaded.DistinctBy(message => message.Id))
			await events.MessageUpdatedAsync(MessageEventMapper.ToSummary(message));
		if (contactSuggestionsChanged)
			await events.ContactsChangedAsync(account.Id);

		logger.LogInformation(
			"Coverage page for mailbox {MailboxId}: {Count} messages, more={HasMore}.",
			mailbox.Id,
			page.Messages.Count,
			page.HasMore
		);

		return page.HasMore;
	}

	public async Task RecordFailureAsync(
		Guid accountId,
		Guid mailboxId,
		int expectedTopologyGeneration,
		int expectedPolicyGeneration,
		Exception ex,
		CancellationToken ct = default
	)
	{
		context.ChangeTracker.Clear();
		var strategy = context.Database.CreateExecutionStrategy();
		var recorded = false;
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			if (!await context.Accounts.AnyAsync(a => a.Id == accountId && a.IsEnabled, ct))
			{
				return;
			}
			if (!await context.Mailboxes.AnyAsync(
					mailbox => mailbox.Id == mailboxId
						&& mailbox.AccountId == accountId
						&& mailbox.ProviderMailboxId != null
						&& mailbox.TopologyGeneration == expectedTopologyGeneration
						&& mailbox.CoveragePolicyGeneration == expectedPolicyGeneration,
					ct
				))
			{
				return;
			}


			var state = await context.MailboxCoverageStates.FirstOrDefaultAsync(
				coverage => coverage.MailboxId == mailboxId,
				ct
			);
			if (state is null)
			{
				return;
			}
			if (state.Status == CoverageStatus.Covered)
			{
				return;
			}


			state.Status = CoverageStatus.Failed;
			state.LastError = ex.Message;
			faults.Reached(FaultPoints.MailboxHealthAfterApplyBeforeCommit);
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
			recorded = true;
		});
		if (recorded)
		{
			await MailboxSummaryDtoFactory.AnnounceAsync(context, events, accountId, mailboxId, ct);
		}
	}

	/// <summary>Runs pages until coverage completes.</summary>
	public async Task RunToCompletionAsync(
		Account account,
		Mailbox mailbox,
		int pageSize = 200,
		CancellationToken ct = default
	)
	{
		while (await RunPageAsync(account, mailbox, pageSize, ct))
		{
			// Each page enqueues its successor in production; here the loop stands in for the
			// self-scheduling job, since job state is bounded by memory and work is enqueued
			// incrementally rather than fanned out up front (§6).
		}
	}

	private async Task<MailboxCoverageState> GetOrCreateAsync(Mailbox mailbox, CancellationToken ct)
	{
		var coverage = await context.MailboxCoverageStates.FirstOrDefaultAsync(c => c.MailboxId == mailbox.Id, ct);
		if (coverage is not null)
		{
			return coverage;
		}

		coverage = new MailboxCoverageState { MailboxId = mailbox.Id, Status = CoverageStatus.NotStarted };
		context.MailboxCoverageStates.Add(coverage);
		return coverage;
	}

	private async Task<Dictionary<string, Mailbox>> MailboxesByProviderIdAsync(Account account, CancellationToken ct) =>
		await context
			.Mailboxes.Where(m => m.AccountId == account.Id && m.ProviderMailboxId != null)
			// Read untracked, so the generation compared against is the one currently in the
			// database rather than a value this context happened to load before the page was
			// issued. Only occurrence writes follow, and those carry the mailbox id by value.
			.AsNoTracking()
			.ToDictionaryAsync(m => m.ProviderMailboxId!, m => m, ct);
}

internal sealed class CoverageBaselinePendingException : Exception;
