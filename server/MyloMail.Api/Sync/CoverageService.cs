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
/// <para>
/// <b>Bounds are coverage targets, not membership limits.</b> "Last N messages" means
/// actively enumerate at least the newest N for that mailbox; messages discovered through
/// another mailbox or the account change stream may also appear there — necessarily so under
/// Gmail's label model, where one message belongs to several mailboxes at once.
/// </para>
/// <para>
/// <b>Gmail walks the account, not each label.</b> Listing every label separately fetched a
/// message once per label it wore, so Gmail's coverage is one account-wide walk
/// (<see cref="RunAccountPageAsync"/>) whose cursor lives on <see cref="AccountCoverageState"/>
/// and whose bound is the account's: the newest N messages overall, or those since a date.
/// Each page fetches a message once and maps its label set onto the account's mailboxes.
/// A label created after the walk started is caught up on its own through
/// <see cref="RunPageAsync"/>, the same per-mailbox path IMAP and Graph use throughout.
/// </para>
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
		if (IsAccountScoped(account) && !await WalkCoveredAsync(account.Id, ct))
		{
			// Before the account-wide walk has covered the account it owns every mailbox. A
			// mailbox page here would walk that label on its own and fetch each message again.
			throw new InvalidOperationException(
				"Gmail mailboxes are paged individually only to catch up labels the account walk did not cover."
			);
		}

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

		// Gmail's range is the account's: one walk serves every label, so a per-label range has
		// nothing to attach to. Overrides are cleared for Gmail by migration and refused by the
		// hub; ignoring one here covers a row written before either existed.
		var mode = IsAccountScoped(account)
			? account.InitialSyncMode
			: mailbox.InitialSyncModeOverride ?? account.InitialSyncMode;
		var bound = IsAccountScoped(account)
			? account.InitialSyncBoundValue
			: mailbox.InitialSyncBoundValueOverride ?? account.InitialSyncBoundValue;

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
		IReadOnlyList<NotificationDto> notificationAnnouncements = [];

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
			if (account.ProviderType == ProviderType.Imap)
			{
				var notificationBaseline = await context
					.ChangeStreamStates.Where(state =>
						state.AccountId == account.Id
						&& state.MailboxId == mailbox.Id
						&& state.CursorState != null
						&& !state.IsRebasing
					)
					.Select(state => (DateTimeOffset?)state.NotificationBaselineAt)
					.FirstOrDefaultAsync(ct);
				if (notificationBaseline is { } baseline)
				{
					notificationAnnouncements = await notifications.RecordEligibleAsync(
						account,
						[.. ingested.Created, .. ingested.Updated],
						baseline,
						ct
					);
				}
			}

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
		await notifications.AnnounceAsync(notificationAnnouncements);

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

	/// <summary>
	/// Whether this account backfills as one account-wide walk rather than one walk per
	/// mailbox (<see cref="IAccountBackfillProvider"/>). Gmail only.
	/// </summary>
	/// <remarks>
	/// Asked of the account, not of the provider: the callers that need the answer — settings,
	/// the hub — would otherwise build a provider to learn what the provider type already says,
	/// and for IMAP building one reads the credential store.
	/// </remarks>
	public static bool IsAccountScoped(Account account) => account.ProviderType == ProviderType.Gmail;

	/// <summary>
	/// One page of an account-scoped provider's coverage: a page of the account-wide walk while
	/// it is incomplete, then — once it has completed — one page of catch-up for a mailbox the
	/// walk did not cover (a label created after it started).
	/// </summary>
	/// <remarks>
	/// One call does one page of one kind of work, so a single self-scheduling job can own all
	/// of an account's coverage and never run two pages at once. The walk's page commits its
	/// data, its resume token and the mailbox rows that mirror it in one transaction; replaying
	/// a page is safe because every write is an upsert.
	/// </remarks>
	/// <returns>True while any of that work remains.</returns>
	public async Task<bool> RunAccountPageAsync(Account account, int pageSize = 200, CancellationToken ct = default)
	{
		if (!IsAccountScoped(account))
		{
			throw new InvalidOperationException("This provider backfills one mailbox at a time.");
		}

		if (await WalkCoveredAsync(account.Id, ct))
		{
			return await RunCatchUpPageAsync(account, pageSize, ct);
		}

		if (await RunWalkPageAsync(account, pageSize, ct))
		{
			return true;
		}

		// The walk is finished. It covered the mailboxes that took part; one that appeared
		// after it started has not been covered and still needs its own catch-up.
		return await HasUncoveredMailboxAsync(account.Id, ct);
	}

	/// <summary>Runs account pages until none remain.</summary>
	public async Task RunAccountToCompletionAsync(Account account, int pageSize = 200, CancellationToken ct = default)
	{
		while (await RunAccountPageAsync(account, pageSize, ct))
		{
			// Stands in for the self-scheduling job, as in RunToCompletionAsync.
		}
	}

	/// <summary>
	/// What an account coverage job is about to work on and the generations it is issued under,
	/// so a failure it raises is recorded only against that work (see
	/// <see cref="RecordAccountFailureAsync"/>).
	/// </summary>
	public async Task<AccountCoverageFence> CaptureFenceAsync(Account account, CancellationToken ct = default)
	{
		var walk = await context
			.AccountCoverageStates.AsNoTracking()
			.FirstOrDefaultAsync(state => state.AccountId == account.Id, ct);
		if (walk is not { Status: CoverageStatus.Covered })
		{
			return new AccountCoverageFence(walk?.PolicyGeneration ?? 0, null);
		}

		var next = await context
			.Mailboxes.AsNoTracking()
			.Where(m => m.AccountId == account.Id && m.ProviderMailboxId != null)
			.Where(m => !context.MailboxCoverageStates.Any(c => c.MailboxId == m.Id && c.Status == CoverageStatus.Covered))
			.OrderBy(m => m.SpecialUse == SpecialUse.Inbox ? 0 : 1)
			.ThenBy(m => m.Id)
			.Select(m => new { m.Id, m.TopologyGeneration, m.CoveragePolicyGeneration })
			.FirstOrDefaultAsync(ct);
		return new AccountCoverageFence(
			walk.PolicyGeneration,
			next is null ? null : new CatchUpFence(next.Id, next.TopologyGeneration, next.CoveragePolicyGeneration)
		);
	}

	/// <summary>
	/// Records a failed account coverage page against whatever it was working on, unless that
	/// work has been restarted since the job was issued.
	/// </summary>
	/// <remarks>
	/// A failed walk is recorded once, on the mailbox that carries its progress, rather than on
	/// every label: the failure is the account's, and one problem per label would be dozens of
	/// identical entries in the problems list.
	/// </remarks>
	public async Task RecordAccountFailureAsync(
		Guid accountId,
		AccountCoverageFence fence,
		Exception ex,
		CancellationToken ct = default
	)
	{
		if (fence.CatchUp is { } catchUp)
		{
			await RecordFailureAsync(
				accountId,
				catchUp.MailboxId,
				catchUp.TopologyGeneration,
				catchUp.PolicyGeneration,
				ex,
				ct
			);
			return;
		}

		context.ChangeTracker.Clear();
		Guid? failedMailboxId = null;
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			if (!await context.Accounts.AnyAsync(a => a.Id == accountId && a.IsEnabled, ct))
			{
				return;
			}

			var walk = await context.AccountCoverageStates.FirstOrDefaultAsync(
				state => state.AccountId == accountId,
				ct
			);
			if (
				walk is null
				|| walk.Status == CoverageStatus.Covered
				|| walk.PolicyGeneration != fence.WalkPolicyGeneration
			)
			{
				return;
			}

			var participantIds = await ParticipantIdsAsync(accountId, ct);
			var carrier = await ProgressCarrierAsync(accountId, participantIds, ct);
			if (carrier is not Guid carrierId)
			{
				return;
			}

			walk.Status = CoverageStatus.Failed;
			walk.LastError = ex.Message;
			await context
				.MailboxCoverageStates.Where(state => state.MailboxId == carrierId)
				.ExecuteUpdateAsync(
					setters =>
						setters
							.SetProperty(state => state.Status, CoverageStatus.Failed)
							.SetProperty(state => state.LastError, ex.Message),
					ct
				);
			faults.Reached(FaultPoints.MailboxHealthAfterApplyBeforeCommit);
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
			failedMailboxId = carrierId;
		});
		if (failedMailboxId is Guid announced)
		{
			await MailboxSummaryDtoFactory.AnnounceAsync(context, events, accountId, announced, ct);
		}
	}

	private async Task<bool> RunCatchUpPageAsync(Account account, int pageSize, CancellationToken ct)
	{
		var pending = await UncoveredMailboxesAsync(account.Id, ct);
		if (pending.Count == 0)
		{
			return false;
		}

		var more = await RunPageAsync(account, pending[0], pageSize, ct);
		return more || pending.Count > 1;
	}

	private async Task<bool> RunWalkPageAsync(Account account, int pageSize, CancellationToken ct)
	{
		if (!await HasGmailBaselineAsync(account.Id, ct))
		{
			throw new CoverageBaselinePendingException();
		}

		var begun = await BeginWalkAsync(account, ct);
		if (begun is null)
		{
			return false;
		}

		if (begun.Surfaced.Count > 0)
		{
			await MailboxSummaryDtoFactory.AnnounceManyAsync(context, events, begun.Surfaced, ct);
		}

		// Read after the walk's own state, never before. A settings change commits the new bound
		// and the new generation together, so reading the bound second can only pair a stale
		// generation with a newer bound — which the generation check at commit discards. The
		// other order could pair a stale bound with the new generation and commit a page
		// fetched under the wrong range.
		var policy = await context
			.Accounts.AsNoTracking()
			.Where(a => a.Id == account.Id)
			.Select(a => new { a.InitialSyncMode, a.InitialSyncBoundValue })
			.SingleAsync(ct);
		var resumeToken = begun.ResumeToken;
		var policyGeneration = begun.PolicyGeneration;

		// Captured before the call: the incarnation of each mailbox this page is fetched for,
		// which is the only thing it is valid to write against.
		var generations = GenerationSnapshot.Capture((await MailboxesByProviderIdAsync(account, ct)).Values);

		var provider = providers.For(account) as IAccountBackfillProvider
			?? throw new InvalidOperationException(
				$"The {account.ProviderType} provider offers no account-wide backfill."
			);
		var page = await provider.InitialSyncAccountAsync(
			account,
			resumeToken,
			policy.InitialSyncMode,
			policy.InitialSyncBoundValue,
			pageSize,
			ct
		);

		faults.Reached(FaultPoints.SyncPageBeforeCommit);

		var remoteDrafts = await drafts.PrepareAsync(
			account,
			page.Messages,
			await MailboxesByProviderIdAsync(account, ct),
			ct
		);
		IReadOnlyList<Guid> changedDraftIds = [];
		IReadOnlyList<Message> rethreaded = [];
		IReadOnlyList<Guid> countedMailboxIds = [];
		var contactSuggestionsChanged = false;
		WalkCommit committed = null!;

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

			var walk = await context.AccountCoverageStates.FirstAsync(state => state.AccountId == account.Id, ct);
			await context.Entry(walk).ReloadAsync(ct);
			if (
				walk.Status == CoverageStatus.Covered
				|| walk.PolicyGeneration != policyGeneration
				|| walk.ResumeToken != resumeToken
			)
			{
				// The walk was restarted, or another walker advanced it, while this page was in
				// flight. Committing would move the cursor from a position it no longer holds.
				throw new CoverageBaselinePendingException();
			}
			if (!await HasGmailBaselineAsync(account.Id, ct))
			{
				throw new CoverageBaselinePendingException();
			}

			// Read inside the transaction: every mailbox's generation, and the mailbox set the
			// page is mapped onto, are the ones this commit writes against.
			var current = await MailboxesByProviderIdAsync(account, ct);
			if (current.Values.Any(mailbox => !generations.StillCurrent(mailbox.ProviderMailboxId, mailbox)))
			{
				// A mailbox was replaced while the page was in flight. Its occurrences would be
				// dropped by the ingestor, and committing the token anyway would skip them.
				throw new CoverageBaselinePendingException();
			}

			// A message with no mapped label has nowhere to live: Gmail has no "All Mail" label
			// and this app has no All Mail mailbox. A membership-less row is a tombstone (§6)
			// that GC collects within minutes, after queueing a raw download for it. It is
			// still counted as consumed below; history brings it in if it is ever labelled.
			var materialisable = page.Messages
				.Where(message => message.Occurrences.Any(o => current.ContainsKey(o.ProviderMailboxId)))
				.ToList();
			var ingested = await ingestor.IngestAsync(account, materialisable, current, generations, ct);
			rethreaded = ingested.Rethreaded;
			countedMailboxIds = ingested.CountedMailboxIds;
			contactSuggestionsChanged = ingested.ContactSuggestionsChanged;
			changedDraftIds = await drafts.ApplyAsync(account, remoteDrafts, current, generations, ct);

			// Same as the per-mailbox page: a message this page materialises may already have a
			// staged-path notification recorded under its provider stable id. Backfill never
			// raises a notification itself.
			var resolvedByProviderStableId = ingested
				.Created.Concat(ingested.Updated)
				.Where(m => m.ProviderStableId is not null)
				.ToDictionary(m => m.ProviderStableId!, m => m.Id);
			await notifications.BackfillMessageIdsAsync(account.Id, resolvedByProviderStableId, ct);

			walk.MessagesFetched += page.Messages.Count;
			walk.EstimatedTotal = page.EstimatedTotal ?? walk.EstimatedTotal;
			walk.ResumeToken = page.HasMore ? page.ResumeToken : null;
			walk.LastError = null;
			walk.Status = page.HasMore ? CoverageStatus.Backfilling : CoverageStatus.Covered;

			var participantIds = await ParticipantIdsAsync(account.Id, ct);
			var carrierId = await ProgressCarrierAsync(account.Id, participantIds, ct);
			await MirrorWalkAsync(walk, participantIds, carrierId, ct);
			committed = new WalkCommit(
				participantIds,
				carrierId,
				walk.MessagesFetched,
				walk.EstimatedTotal,
				walk.Status
			);

			faults.Reached(FaultPoints.SyncPageAfterApplyBeforeCommit);
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
		});

		faults.Reached(FaultPoints.SyncPageAfterCommit);

		// After the page is committed, never before: an event announcing progress that a crash
		// then discarded would leave the UI ahead of the database.
		foreach (var mailboxId in committed.ParticipantIds)
		{
			var carriesWalk = mailboxId == committed.CarrierId;
			await events.SyncProgressAsync(
				new SyncProgressDto(
					mailboxId,
					SyncProgressKind.Coverage,
					committed.Status,
					carriesWalk ? committed.MessagesFetched : 0,
					carriesWalk ? committed.EstimatedTotal : 0
				)
			);
		}

		var changedMailboxIds = new HashSet<Guid>(countedMailboxIds);
		if (committed.CarrierId is Guid carrier)
		{
			changedMailboxIds.Add(carrier);
		}
		if (committed.Status == CoverageStatus.Covered)
		{
			changedMailboxIds.UnionWith(committed.ParticipantIds);
		}
		await MailboxSummaryDtoFactory.AnnounceManyAsync(context, events, changedMailboxIds, ct);
		foreach (var draftId in changedDraftIds)
		{
			await events.DraftUpdatedAsync(draftId);
		}
		foreach (var message in rethreaded.DistinctBy(message => message.Id))
			await events.MessageUpdatedAsync(MessageEventMapper.ToSummary(message));
		if (contactSuggestionsChanged)
			await events.ContactsChangedAsync(account.Id);

		logger.LogInformation(
			"Account coverage page for account {AccountId}: {Count} messages, more={HasMore}.",
			account.Id,
			page.Messages.Count,
			page.HasMore
		);

		return page.HasMore;
	}

	/// <summary>
	/// Brings the walk into its running state, in one transaction with the mailbox rows that
	/// follow it.
	/// </summary>
	/// <remarks>
	/// A fresh walk — never started, restarted, or left with no participating mailbox — makes
	/// every provider-backed mailbox a participant and zeroes its progress. A walk resuming
	/// after a failure only brings the failed row back to Backfilling. The decision is taken
	/// from state re-read inside the transaction, never from what the job loaded earlier, so a
	/// reset that committed in between cannot be answered with the pre-reset decision.
	/// </remarks>
	/// <returns>Null when there is nothing to walk: it is already covered, or there are no mailboxes.</returns>
	private async Task<WalkStart?> BeginWalkAsync(Account account, CancellationToken ct)
	{
		var snapshot = await ReadWalkAsync(account.Id, ct);
		if (snapshot.Status == CoverageStatus.Covered)
		{
			return null;
		}
		if (
			snapshot.Status == CoverageStatus.Backfilling
			&& (await ParticipantIdsAsync(account.Id, ct)).Count > 0
		)
		{
			return new WalkStart(snapshot.ResumeToken, snapshot.PolicyGeneration, []);
		}

		WalkStart? start = null;
		var strategy = context.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			await using var transaction = await context.Database.BeginTransactionAsync(ct);
			var walk = await context.AccountCoverageStates.FirstAsync(state => state.AccountId == account.Id, ct);
			await context.Entry(walk).ReloadAsync(ct);
			if (walk.Status == CoverageStatus.Covered)
			{
				return;
			}

			var mailboxIds = await context
				.Mailboxes.Where(m => m.AccountId == account.Id && m.ProviderMailboxId != null)
				.Select(m => m.Id)
				.ToListAsync(ct);
			if (mailboxIds.Count == 0)
			{
				return;
			}

			var now = clock.GetUtcNow();
			var participantIds = await ParticipantIdsAsync(account.Id, ct);
			var surfaced = new List<Guid>();
			if (walk.Status == CoverageStatus.NotStarted || participantIds.Count == 0)
			{
				var existing = await context
					.MailboxCoverageStates.Where(state => mailboxIds.Contains(state.MailboxId))
					.Select(state => state.MailboxId)
					.ToListAsync(ct);
				foreach (var mailboxId in mailboxIds.Except(existing))
				{
					context.MailboxCoverageStates.Add(
						new MailboxCoverageState
						{
							MailboxId = mailboxId,
							Status = CoverageStatus.Backfilling,
							EstimatedTotal = 0,
							StartedAt = now,
						}
					);
				}
				await context
					.MailboxCoverageStates.Where(state => existing.Contains(state.MailboxId))
					.ExecuteUpdateAsync(
						setters =>
							setters
								.SetProperty(state => state.Status, CoverageStatus.Backfilling)
								.SetProperty(state => state.ResumeToken, (string?)null)
								.SetProperty(state => state.MessagesFetched, 0)
								.SetProperty(state => state.EstimatedTotal, (int?)0)
								.SetProperty(state => state.StartedAt, (DateTimeOffset?)now)
								.SetProperty(state => state.LastError, (string?)null),
						ct
					);
				walk.ResumeToken = null;
				walk.MessagesFetched = 0;
				walk.EstimatedTotal = null;
				walk.StartedAt = now;
				surfaced.AddRange(mailboxIds);
			}
			else if (walk.Status == CoverageStatus.Failed)
			{
				surfaced.AddRange(
					await context
						.MailboxCoverageStates.Where(state =>
							participantIds.Contains(state.MailboxId) && state.Status == CoverageStatus.Failed
						)
						.Select(state => state.MailboxId)
						.ToListAsync(ct)
				);
				await context
					.MailboxCoverageStates.Where(state =>
						participantIds.Contains(state.MailboxId) && state.Status == CoverageStatus.Failed
					)
					.ExecuteUpdateAsync(
						setters => setters.SetProperty(state => state.Status, CoverageStatus.Backfilling),
						ct
					);
			}

			walk.Status = CoverageStatus.Backfilling;
			walk.StartedAt ??= now;
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
			start = new WalkStart(walk.ResumeToken, walk.PolicyGeneration, surfaced);
		});
		return start;
	}

	/// <summary>
	/// Writes the walk's status onto the mailboxes that took part in it. Called inside the
	/// page's transaction, so a mailbox row never claims a state the walk has not committed.
	/// </summary>
	/// <remarks>
	/// The walk's progress is reported once, on the carrier mailbox; every other participant
	/// reports 0 of 0. The status bar adds up every mailbox's figures, so repeating the
	/// account's totals on each label would count each message once per label and show a total
	/// several times the size of the mailbox.
	/// </remarks>
	private async Task MirrorWalkAsync(
		AccountCoverageState walk,
		IReadOnlyList<Guid> participantIds,
		Guid? carrierId,
		CancellationToken ct
	)
	{
		var status = walk.Status;
		var others = participantIds.Where(id => id != carrierId).ToList();
		await context
			.MailboxCoverageStates.Where(state => others.Contains(state.MailboxId))
			.ExecuteUpdateAsync(
				setters =>
					setters
						.SetProperty(state => state.Status, status)
						.SetProperty(state => state.MessagesFetched, 0)
						.SetProperty(state => state.EstimatedTotal, (int?)0)
						.SetProperty(state => state.LastError, (string?)null),
				ct
			);
		if (carrierId is not Guid carrier)
		{
			return;
		}

		var fetched = walk.MessagesFetched;
		var estimated = walk.EstimatedTotal;
		await context
			.MailboxCoverageStates.Where(state => state.MailboxId == carrier)
			.ExecuteUpdateAsync(
				setters =>
					setters
						.SetProperty(state => state.Status, status)
						.SetProperty(state => state.MessagesFetched, fetched)
						.SetProperty(state => state.EstimatedTotal, estimated)
						.SetProperty(state => state.LastError, (string?)null),
				ct
			);
	}

	/// <summary>
	/// The mailbox that reports the walk's progress: the Inbox when it took part, otherwise the
	/// first participant by id. Recomputed on every page, so a change of carrier moves the
	/// figures to the new one at once rather than reporting them twice.
	/// </summary>
	private async Task<Guid?> ProgressCarrierAsync(
		Guid accountId,
		IReadOnlyList<Guid> participantIds,
		CancellationToken ct
	)
	{
		var carrier = await context
			.Mailboxes.AsNoTracking()
			.Where(m => m.AccountId == accountId && participantIds.Contains(m.Id))
			.OrderBy(m => m.SpecialUse == SpecialUse.Inbox ? 0 : 1)
			.ThenBy(m => m.Id)
			.Select(m => new { m.Id })
			.FirstOrDefaultAsync(ct);
		return carrier?.Id;
	}

	/// <summary>
	/// The provider-backed mailboxes taking part in the running walk: those it has marked
	/// Backfilling, or Failed after an error. While the walk is incomplete no other path writes
	/// these states, so membership needs no marker of its own.
	/// </summary>
	private Task<List<Guid>> ParticipantIdsAsync(Guid accountId, CancellationToken ct) =>
		(
			from coverage in context.MailboxCoverageStates.AsNoTracking()
			join mailbox in context.Mailboxes on coverage.MailboxId equals mailbox.Id
			where mailbox.AccountId == accountId
				&& mailbox.ProviderMailboxId != null
				&& (coverage.Status == CoverageStatus.Backfilling || coverage.Status == CoverageStatus.Failed)
			select coverage.MailboxId
		).ToListAsync(ct);

	/// <summary>
	/// The walk's state, created as <see cref="CoverageStatus.NotStarted"/> the first time an
	/// account needs one. Existing databases are seeded by migration instead (§1 migration
	/// note), so a missing row here is a new account, never a finished one.
	/// </summary>
	private async Task<AccountCoverageState> ReadWalkAsync(Guid accountId, CancellationToken ct)
	{
		var walk = await context
			.AccountCoverageStates.AsNoTracking()
			.FirstOrDefaultAsync(state => state.AccountId == accountId, ct);
		if (walk is not null)
		{
			return walk;
		}

		walk = new AccountCoverageState { AccountId = accountId, Status = CoverageStatus.NotStarted };
		context.AccountCoverageStates.Add(walk);
		await context.SaveChangesAsync(ct);
		return walk;
	}

	private Task<bool> WalkCoveredAsync(Guid accountId, CancellationToken ct) =>
		context
			.AccountCoverageStates.AsNoTracking()
			.AnyAsync(state => state.AccountId == accountId && state.Status == CoverageStatus.Covered, ct);

	private Task<bool> HasGmailBaselineAsync(Guid accountId, CancellationToken ct) =>
		context.ChangeStreamStates.AnyAsync(
			state =>
				state.AccountId == accountId
				&& state.MailboxId == null
				&& state.CursorState != null
				&& !state.IsRebasing,
			ct
		);

	private Task<bool> HasUncoveredMailboxAsync(Guid accountId, CancellationToken ct) =>
		context
			.Mailboxes.Where(m => m.AccountId == accountId && m.ProviderMailboxId != null)
			.AnyAsync(
				m => !context.MailboxCoverageStates.Any(c => c.MailboxId == m.Id && c.Status == CoverageStatus.Covered),
				ct
			);

	private Task<List<Mailbox>> UncoveredMailboxesAsync(Guid accountId, CancellationToken ct) =>
		context
			.Mailboxes.Where(m => m.AccountId == accountId && m.ProviderMailboxId != null)
			.Where(m => !context.MailboxCoverageStates.Any(c => c.MailboxId == m.Id && c.Status == CoverageStatus.Covered))
			.OrderBy(m => m.SpecialUse == SpecialUse.Inbox ? 0 : 1)
			.ThenBy(m => m.Id)
			.ToListAsync(ct);

	private sealed record WalkStart(string? ResumeToken, int PolicyGeneration, IReadOnlyList<Guid> Surfaced);

	private sealed record WalkCommit(
		IReadOnlyList<Guid> ParticipantIds,
		Guid? CarrierId,
		int MessagesFetched,
		int? EstimatedTotal,
		CoverageStatus Status
	);

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

/// <summary>
/// What an account coverage job was issued against. A failure the job raises is recorded only if
/// that work has not been restarted since — the walk's generation for the account-wide walk, a
/// mailbox's own generations for a catch-up — so a stale failure cannot mark a replacement
/// failed.
/// </summary>
public sealed record AccountCoverageFence(int WalkPolicyGeneration, CatchUpFence? CatchUp);

/// <summary>The mailbox a catch-up page is about to work on, with the generations it was issued under.</summary>
public sealed record CatchUpFence(Guid MailboxId, int TopologyGeneration, int PolicyGeneration);
