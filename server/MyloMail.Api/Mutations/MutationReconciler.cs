using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Contracts;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Mutations;

/// <summary>Settles dispatched mutation attempts whose provider outcome was never persisted.</summary>
/// <remarks>
/// A durable dispatch proves only that the provider <em>may</em> have seen a request. This
/// service therefore observes the server before deciding whether a move or deletion is done;
/// it never promotes an attempt to success from its local state alone.
/// </remarks>
public sealed class MutationReconciler(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	MutationChainEvaluator chains,
	TimeProvider clock,
	IHubEvents events,
	ILogger<MutationReconciler> logger
)
{
	public async Task<int> ReconcileAsync(Guid accountId, CancellationToken ct = default)
	{
		var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
		if (account is null)
		{
			return 0;
		}

		var attempts = await context.MutationExecutionAttempts
			.Where(a => a.AccountId == accountId
				&& (a.State == MutationAttemptState.Dispatched || a.State == MutationAttemptState.Ambiguous)
				&& a.ResultPersistedAt == null
				&& a.OutboxItemId == null)
			.Include(a => a.Items)
			.ToListAsync(ct);
		var itemIds = attempts.SelectMany(a => a.Items).Select(i => i.MutationItemId).Distinct().ToList();
		var items = await context.MutationItems.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

		var settled = 0;
		foreach (var attempt in attempts)
		{
			var memberships = attempt.Items.ToDictionary(membership => membership.MutationItemId);
			var unresolved = attempt.Items
				.Select(membership => items[membership.MutationItemId])
				.Where(item => item.State is MutationState.Pending or MutationState.Leased)
				.ToList();
			if (unresolved.Count == 0)
			{
				attempt.State = MutationAttemptState.Completed;
				attempt.ResultPersistedAt = clock.GetUtcNow();
				await context.SaveChangesAsync(ct);
				settled++;
				continue;
			}

			var policy = MutationRecoveryPolicyResolver.For(attempt.OperationKind, providers.For(account).Capabilities);

			if (policy == MutationRecoveryPolicy.AmbiguousOutcome)
			{
				// Only send resolves to this, and it belongs to SendReconciler. Reaching here
				// means the OutboxItemId filter above has stopped holding, so this refuses
				// rather than reconciling a send under move semantics.
				logger.LogError(
					"Attempt {AttemptId} resolved to an ambiguous-outcome policy in the mutation reconciler, "
						+ "which does not own that recovery path.",
					attempt.Id
				);
				continue;
			}
			if (policy == MutationRecoveryPolicy.RetrySafe)
			{
				await RequeueAsync(attempt, unresolved, ct);
				settled++;
				continue;
			}

			var observed = new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>();
			foreach (var item in unresolved)
			{
				observed[item.Id] = await ObserveAsync(account, item.MessageId, ct);
			}

			var removedMessageIds = new List<Guid>();
			var confirmedMutations = new List<MutationSettledDto>();
			foreach (var item in unresolved)
			{
				var locations = observed[item.Id];
				var resolvedTargetMailboxId = memberships[item.Id].ResolvedTargetMailboxId;
				locations = await CompletePartialImapTrashMoveAsync(
					account,
					item,
					resolvedTargetMailboxId,
					locations,
					ct
				);
				var applied = IsApplied(account, item, resolvedTargetMailboxId, locations);
				if (applied)
				{
					if (await SynchroniseLocationsAsync(item.MessageId, locations, ct))
					{
						removedMessageIds.Add(item.MessageId);
					}

					confirmedMutations.Add(
						new MutationSettledDto(item.Id, item.MessageId, item.OperationKind)
					);
					item.State = MutationState.Completed;
					item.CompletedAt = clock.GetUtcNow();
					item.LeaseOwner = null;
					item.LeaseExpiresAt = null;
					await chains.ClearDesiredStateAsync(item, ct);
				}
				else
				{
					await RequeueItemAsync(item, ct);
				}
			}

			attempt.State = MutationAttemptState.Completed;
			attempt.ResultPersistedAt = clock.GetUtcNow();
			await context.SaveChangesAsync(ct);
			var deleted = await MessageChangeAnnouncer.AnnounceDeletedAsync(
				context,
				events,
				removedMessageIds,
				ct
			);
			await MessageChangeAnnouncer.AnnounceUpdatedAsync(
				context,
				events,
				[
					.. confirmedMutations
						.Where(settlement => !deleted.Contains(settlement.MessageId))
						.Select(settlement => settlement.MessageId),
				],
				ct
			);
			foreach (var settlement in confirmedMutations)
			{
				await events.MessageMutationSettledAsync(settlement);
			}
			settled++;
		}

		if (settled > 0)
		{
			logger.LogInformation("Reconciled {Attempts} ambiguous mutation attempts for account {AccountId}.", settled, accountId);
		}
		return settled;
	}

	private async Task RequeueAsync(
		MutationExecutionAttempt attempt,
		IReadOnlyCollection<MutationItem> unresolved,
		CancellationToken ct
	)
	{
		foreach (var item in unresolved)
		{
			await RequeueItemAsync(item, ct);
		}
		attempt.State = MutationAttemptState.Completed;
		attempt.ResultPersistedAt = clock.GetUtcNow();
		await context.SaveChangesAsync(ct);
	}

	private static Task RequeueItemAsync(MutationItem item, CancellationToken ct)
	{
		item.State = MutationState.Pending;
		item.LeaseOwner = null;
		item.LeaseExpiresAt = null;
		return Task.CompletedTask;
	}

	private async Task<IReadOnlyDictionary<Guid, string>> CompletePartialImapTrashMoveAsync(
		Account account,
		MutationItem item,
		Guid? resolvedTargetMailboxId,
		IReadOnlyDictionary<Guid, string> locations,
		CancellationToken ct
	)
	{
		if (account.ProviderType != ProviderType.Imap
			|| item.OperationKind != MutationOperationKind.MoveToTrash)
		{
			return locations;
		}

		if (resolvedTargetMailboxId is not Guid trash || !locations.ContainsKey(trash))
		{
			return locations;
		}

		var source = await context
			.MessageMailboxes.Where(occurrence => occurrence.MessageId == item.MessageId)
			.OrderBy(occurrence => occurrence.MailboxId)
			.FirstOrDefaultAsync(ct);
		if (source is null || source.MailboxId == trash)
		{
			return locations;
		}

		// A basic IMAP server may have completed COPY but crashed before marking and
		// expunging the source. The durable original dispatch already covers this
		// continuation. Address only the still-persisted source UID selected for the original
		// execution; heuristic Message-ID matches in other folders are observations, not proof
		// that those messages belong to this mutation.
		var sourceReference = new MessageOccurrenceRef(
			item.MessageId,
			source.MailboxId,
			source.ProviderOccurrenceId
		);
		var provider = providers.For(account);
		var sourceMailbox = await context.Mailboxes.FirstAsync(
			mailbox => mailbox.Id == source.MailboxId,
			ct
		);
		var sourceSnapshot = await provider.GetMailboxIntegritySnapshotAsync(
			account,
			sourceMailbox,
			[sourceReference],
			ct
		);
		if (sourceSnapshot.ExistingOccurrenceIds.Contains(source.ProviderOccurrenceId))
		{
			await provider.RemoveFromMailboxAsync(account, [sourceReference], ct);
			sourceSnapshot = await provider.GetMailboxIntegritySnapshotAsync(
				account,
				sourceMailbox,
				[sourceReference],
				ct
			);
		}
		var after = await ObserveAsync(account, item.MessageId, ct);
		if (sourceSnapshot.ExistingOccurrenceIds.Contains(source.ProviderOccurrenceId)
			|| !after.TryGetValue(trash, out var trashProviderId))
		{
			return after;
		}

		// The exact source UID is gone and Trash is present, so the intent is complete.
		// Do not attach other heuristic Message-ID matches to this canonical message.
		return new Dictionary<Guid, string> { [trash] = trashProviderId };
	}

	private static bool IsApplied(
		Account account,
		MutationItem item,
		Guid? resolvedTargetMailboxId,
		IReadOnlyDictionary<Guid, string> locations
	)
	{
		switch (item.OperationKind)
		{
			case MutationOperationKind.MoveMessage:
				return item.TargetMailboxId is Guid target
					&& locations.ContainsKey(target)
					&& (account.ProviderType != ProviderType.Imap || locations.Count == 1);
			case MutationOperationKind.MoveToTrash:
				return resolvedTargetMailboxId is Guid trash
					&& locations.ContainsKey(trash)
					&& (account.ProviderType != ProviderType.Imap || locations.Count == 1);
			case MutationOperationKind.RemoveFromMailbox:
				return item.ScopeMailboxId is Guid scope && !locations.ContainsKey(scope);
			case MutationOperationKind.DeletePermanently:
				return locations.Count == 0;
			default:
				return false;
		}
	}

	private async Task<IReadOnlyDictionary<Guid, string>> ObserveAsync(Account account, Guid messageId, CancellationToken ct)
	{
		var message = await context.Messages.FirstAsync(m => m.Id == messageId, ct);
		var known = await (
			from occurrence in context.MessageMailboxes
			join knownMailbox in context.Mailboxes on occurrence.MailboxId equals knownMailbox.Id
			where occurrence.MessageId == messageId && knownMailbox.ProviderMailboxId != null
			select new { knownMailbox.ProviderMailboxId, occurrence.ProviderOccurrenceId }
		).ToDictionaryAsync(
			entry => (entry.ProviderMailboxId!, entry.ProviderOccurrenceId),
			entry => true,
			ct
		);
		var mailboxes = await context.Mailboxes.Where(m => m.AccountId == account.Id && m.ProviderMailboxId != null).ToListAsync(ct);
		var locations = new Dictionary<Guid, string>();
		var provider = providers.For(account);

		foreach (var mailbox in mailboxes)
		{
			string? token = null;
			do
			{
				var page = await provider.InitialSyncMailboxAsync(account, mailbox, token, InitialSyncMode.Full, null, 200, ct);
				foreach (var dto in page.Messages.Where(dto => Matches(message, known, dto)))
				{
					var occurrence = dto.Occurrences.FirstOrDefault(o => o.ProviderMailboxId == mailbox.ProviderMailboxId);
					if (occurrence is not null)
					{
						locations[mailbox.Id] = occurrence.ProviderOccurrenceId;
					}
				}
				token = page.ResumeToken;
			} while (token is not null);
		}

		return locations;
	}

	private static bool Matches(
		Message message,
		IReadOnlyDictionary<(string ProviderMailboxId, string ProviderOccurrenceId), bool> known,
		MessageDto dto
	) =>
		(message.ProviderStableId is not null && message.ProviderStableId == dto.ProviderStableId)
		|| dto.Occurrences.Any(occurrence => known.ContainsKey((occurrence.ProviderMailboxId, occurrence.ProviderOccurrenceId)))
		|| (message.MessageIdHeader is not null
			&& message.MessageIdHeader == dto.MessageIdHeader
			&& message.ReceivedAt == dto.ReceivedAt);

	private async Task<bool> SynchroniseLocationsAsync(Guid messageId, IReadOnlyDictionary<Guid, string> locations, CancellationToken ct)
	{
		var existing = await context.MessageMailboxes.Where(o => o.MessageId == messageId).ToListAsync(ct);
		var vanished = existing.Where(o => !locations.ContainsKey(o.MailboxId)).ToList();
		foreach (var occurrence in vanished)
		{
			context.MessageMailboxes.Remove(occurrence);
		}
		if (locations.Count > 0)
		{
			// Membership survives (or is regained) here — no longer a GC candidate (§6).
			var message = await context.Messages.FirstAsync(m => m.Id == messageId, ct);
			message.OrphanedAt = null;
		}

		foreach (var (mailboxId, providerId) in locations)
		{
			var occurrence = existing.FirstOrDefault(o => o.MailboxId == mailboxId);
			if (occurrence is null)
			{
				context.MessageMailboxes.Add(new MessageMailbox { Id = Guid.NewGuid(), MessageId = messageId, MailboxId = mailboxId, ProviderOccurrenceId = providerId });
			}
			else
			{
				occurrence.ProviderOccurrenceId = providerId;
			}
		}

		return vanished.Count > 0;
	}
}
