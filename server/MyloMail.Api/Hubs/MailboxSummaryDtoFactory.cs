using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Persistence;

namespace MyloMail.Api.Hubs;

/// <summary>
/// Projects the sync state machines into the two independent values the renderer consumes:
/// current usability and historical coverage (§1).
/// </summary>
internal static class MailboxSummaryDtoFactory
{
	public static Task<IReadOnlyList<MailboxSummaryDto>> ListAsync(
		MyloMailDbContext context,
		Guid accountId,
		CancellationToken ct = default
	) => LoadAsync(context, accountId, mailboxId: null, ct);

	public static async Task<MailboxSummaryDto?> GetAsync(
		MyloMailDbContext context,
		Guid mailboxId,
		CancellationToken ct = default
	)
	{
		var accountId = await context
			.Mailboxes.Where(mailbox => mailbox.Id == mailboxId)
			.Select(mailbox => (Guid?)mailbox.AccountId)
			.FirstOrDefaultAsync(ct);
		if (accountId is null)
		{
			return null;
		}

		return (await LoadAsync(context, accountId.Value, mailboxId, ct)).SingleOrDefault();
	}

	public static async Task AnnounceAsync(
		MyloMailDbContext context,
		IHubEvents events,
		Guid accountId,
		Guid? mailboxId,
		CancellationToken ct = default
	)
	{
		var summaries = mailboxId is Guid id
			? (await GetAsync(context, id, ct)) is { } summary
				? [summary]
				: []
			: await ListAsync(context, accountId, ct);
		foreach (var projectedMailbox in summaries)
		{
			await events.MailboxUpdatedAsync(projectedMailbox);
		}
	}

	private static async Task<IReadOnlyList<MailboxSummaryDto>> LoadAsync(
		MyloMailDbContext context,
		Guid accountId,
		Guid? mailboxId,
		CancellationToken ct
	)
	{
		var account = await context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return [];
		}

		var mailboxRows = await context
			.Mailboxes.Where(mailbox =>
				mailbox.AccountId == accountId && (mailboxId == null || mailbox.Id == mailboxId)
			)
			.AsNoTracking()
			.Select(mailbox => new
			{
				Mailbox = mailbox,
				LocalCount = context.MessageMailboxes.Count(occurrence => occurrence.MailboxId == mailbox.Id),
			})
			.ToListAsync(ct);
		if (mailboxRows.Count == 0)
		{
			return [];
		}

		var mailboxIds = mailboxRows.Select(row => row.Mailbox.Id).ToList();
		var coverages = await context
			.MailboxCoverageStates.Where(state => mailboxIds.Contains(state.MailboxId))
			.AsNoTracking()
			.ToDictionaryAsync(state => state.MailboxId, ct);
		var integrity = await context
			.IntegrityReconciliationStates.Where(state => mailboxIds.Contains(state.MailboxId))
			.AsNoTracking()
			.ToDictionaryAsync(state => state.MailboxId, ct);
		var streams = await context
			.ChangeStreamStates.Where(state =>
				state.AccountId == accountId
				&& (state.MailboxId == null || mailboxIds.Contains(state.MailboxId.Value))
			)
			.AsNoTracking()
			.ToListAsync(ct);
		var accountStream = streams.SingleOrDefault(state => state.MailboxId == null);
		var mailboxStreams = streams
			.Where(state => state.MailboxId is not null)
			.ToDictionary(state => state.MailboxId!.Value);
		var topology = await context
			.MailboxTopologySyncStates.AsNoTracking()
			.FirstOrDefaultAsync(state => state.AccountId == accountId, ct);

		return
		[
			.. mailboxRows
				.OrderBy(row => row.Mailbox.LocalSortOrder)
				.Select(row =>
				{
					coverages.TryGetValue(row.Mailbox.Id, out var coverage);
					integrity.TryGetValue(row.Mailbox.Id, out var integrityState);
					mailboxStreams.TryGetValue(row.Mailbox.Id, out var mailboxStream);
					var stream = account.ProviderType == ProviderType.Gmail ? accountStream : mailboxStream;
					var availability = MailboxAvailabilityProjection.Evaluate(
						account,
						row.Mailbox,
						coverage,
						topology,
						stream,
						integrityState
					);
					return new MailboxSummaryDto(
						row.Mailbox.Id,
						row.Mailbox.AccountId,
						row.Mailbox.ParentId,
						row.Mailbox.Name,
						row.Mailbox.SpecialUse,
						row.Mailbox.ProviderTotalCount,
						row.Mailbox.ProviderUnreadCount,
						row.LocalCount,
						availability,
						coverage?.Status ?? CoverageStatus.NotStarted,
						row.Mailbox.IsCollapsed,
						row.Mailbox.InitialSyncModeOverride,
						row.Mailbox.InitialSyncBoundValueOverride,
						row.Mailbox.ProviderMailboxId is null,
						row.Mailbox.SpecialUseOverride
					);
				}),
		];
	}
}

internal static class MailboxAvailabilityProjection
{
	public static MailboxAvailability Evaluate(
		Account account,
		Mailbox mailbox,
		MailboxCoverageState? coverage,
		MailboxTopologySyncState? topology,
		ChangeStreamState? stream,
		IntegrityReconciliationState? integrity
	)
	{
		if (!account.IsEnabled)
		{
			return MailboxAvailability.Unavailable;
		}

		// A synthetic Gmail hierarchy node is local navigation, not provider-backed work. It has
		// no coverage row by design and remains usable as a container.
		if (mailbox.ProviderMailboxId is not null
			&& coverage?.Status is null or CoverageStatus.NotStarted)
		{
			return MailboxAvailability.Unavailable;
		}

		if (account.AuthState != AuthState.Connected
			|| coverage?.Status == CoverageStatus.Failed
			|| topology?.LastError is not null
			|| stream?.IsRebasing == true
			|| stream?.LastError is not null
			|| integrity?.LastError is not null)
		{
			return MailboxAvailability.Degraded;
		}

		// Backfilling is intentionally usable. Coverage reports completeness; it does not gate
		// the already-materialised mailbox view.
		return MailboxAvailability.Usable;
	}
}
