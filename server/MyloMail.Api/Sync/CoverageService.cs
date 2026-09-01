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

		var mode = mailbox.InitialSyncModeOverride ?? account.InitialSyncMode;
		var bound = mailbox.InitialSyncBoundValueOverride ?? account.InitialSyncBoundValue;

		coverage.Status = CoverageStatus.Backfilling;
		coverage.StartedAt ??= clock.GetUtcNow();
		await context.SaveChangesAsync(ct);

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

		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);

			var ingested = await ingestor.IngestAsync(account, page.Messages, mailboxes, generations, ct);
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

			// Backfill raises no per-message events: this is a backlog the user already has,
			// and announcing it would be the notification flood §13 Epic 9 rules out.
			coverage.MessagesFetched += ingested.Created.Count + ingested.Updated.Count;
			coverage.EstimatedTotal = page.EstimatedTotal ?? coverage.EstimatedTotal;
			coverage.ResumeToken = page.ResumeToken;
			coverage.LastError = null;

			if (!page.HasMore)
			{
				coverage.Status = CoverageStatus.Covered;
			}

			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});

		faults.Reached(FaultPoints.SyncPageAfterCommit);

		// After the page is committed, never before: an event announcing progress that a crash
		// then discarded would leave the UI ahead of the database.
		await events.SyncProgressAsync(
			new SyncProgressDto(mailbox.Id, coverage.Status, coverage.MessagesFetched, coverage.EstimatedTotal)
		);
		foreach (var draftId in changedDraftIds)
		{
			await events.DraftUpdatedAsync(draftId);
		}

		logger.LogInformation(
			"Coverage page for mailbox {MailboxId}: {Count} messages, more={HasMore}.",
			mailbox.Id,
			page.Messages.Count,
			page.HasMore
		);

		return page.HasMore;
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
