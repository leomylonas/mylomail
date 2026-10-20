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

			var observed = new Dictionary<Guid, MutationObservation>();
			foreach (var item in unresolved)
			{
				observed[item.Id] = await ObserveAsync(account, item.MessageId, ct);
			}

			var removedMessageIds = new List<Guid>();
			var confirmedMutations = new List<MutationSettledDto>();
			var attemptIndeterminate = false;
			foreach (var item in unresolved)
			{
				var observation = observed[item.Id];
				if (observation.IsIndeterminate)
				{
					attemptIndeterminate = true;
					continue;
				}

				var resolvedTargetMailboxId = memberships[item.Id].ResolvedTargetMailboxId;
				observation = await CompletePartialImapTrashMoveAsync(
					account,
					item,
					resolvedTargetMailboxId,
					observation,
					ct
				);
				if (observation.IsIndeterminate)
				{
					attemptIndeterminate = true;
					continue;
				}

				var applied = IsApplied(
					account,
					item,
					resolvedTargetMailboxId,
					observation.Locations,
					observation.ExistsWithoutLocation
				);
				if (applied)
				{
					if (await SynchroniseLocationsAsync(item.MessageId, observation.Locations, ct))
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

			attempt.State = attemptIndeterminate
				? MutationAttemptState.Ambiguous
				: MutationAttemptState.Completed;
			attempt.ResultPersistedAt = attemptIndeterminate ? null : clock.GetUtcNow();
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
			if (!attemptIndeterminate)
			{
				settled++;
			}
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

	private async Task<MutationObservation> CompletePartialImapTrashMoveAsync(
		Account account,
		MutationItem item,
		Guid? resolvedTargetMailboxId,
		MutationObservation observation,
		CancellationToken ct
	)
	{
		var locations = observation.Locations;
		if (account.ProviderType != ProviderType.Imap
			|| item.OperationKind != MutationOperationKind.MoveToTrash)
		{
			return observation;
		}

		if (resolvedTargetMailboxId is not Guid trash || !locations.ContainsKey(trash))
		{
			return observation;
		}

		var source = await context
			.MessageMailboxes.Where(occurrence => occurrence.MessageId == item.MessageId)
			.OrderBy(occurrence => occurrence.MailboxId)
			.FirstOrDefaultAsync(ct);
		if (source is null || source.MailboxId == trash)
		{
			return observation;
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
		if (
			after.IsIndeterminate
			|| sourceSnapshot.ExistingOccurrenceIds.Contains(source.ProviderOccurrenceId)
			|| !after.Locations.TryGetValue(trash, out var trashProviderId)
		)
		{
			return after;
		}

		// The exact source UID is gone and Trash is present, so the intent is complete.
		// Do not attach other heuristic Message-ID matches to this canonical message.
		return new MutationObservation(
			new Dictionary<Guid, string> { [trash] = trashProviderId },
			false
		);
	}

	private static bool IsApplied(
		Account account,
		MutationItem item,
		Guid? resolvedTargetMailboxId,
		IReadOnlyDictionary<Guid, string> locations,
		bool existsWithoutLocation
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
				return locations.Count == 0 && !existsWithoutLocation;
			default:
				return false;
		}
	}

	/// <summary>
	/// Observes the provider for execution-time recovery of a permanent delete whose local
	/// membership is temporarily absent.
	/// </summary>
	internal Task<MutationObservation> ObserveForExecutionAsync(
		Account account,
		Guid messageId,
		CancellationToken ct = default
	) => ObserveAsync(account, messageId, ct);

	/// <summary>
	/// Whether the provider, asked now, has no such message at all — no copy in any folder and none
	/// without a folder. Only that observation lets a permanent delete count as done when the local
	/// copy has no occurrences left: a Gmail message with no labels has none either, and still exists.
	/// An indeterminate or failed lookup is not "gone".
	/// </summary>
	public async Task<bool> ConfirmGoneAsync(Account account, Guid messageId, CancellationToken ct = default)
	{
		var observation = await ObserveAsync(account, messageId, ct);
		return !observation.IsIndeterminate && observation.Locations.Count == 0 && !observation.ExistsWithoutLocation;
	}

	private async Task<MutationObservation> ObserveAsync(
		Account account,
		Guid messageId,
		CancellationToken ct
	)
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
		var isIndeterminate = false;
		var provider = providers.For(account);

		// Ask the provider about this one message, never walk the account to find it (see
		// IMailProvider.FindMessageAsync): a walk of a large account is slow and rate-limited, and
		// recovery that cannot finish blocks everything behind it.
		var candidatesFound = await provider.FindMessageAsync(
			account,
			new MessageLookup(
				message.ProviderStableId,
				message.MessageIdHeader,
				[
					.. (
						from occurrence in context.MessageMailboxes.AsNoTracking()
						join knownMailbox in context.Mailboxes.AsNoTracking() on occurrence.MailboxId equals knownMailbox.Id
						where occurrence.MessageId == messageId && knownMailbox.ProviderMailboxId != null
						select new { knownMailbox, occurrence.ProviderOccurrenceId }
					)
						.AsEnumerable()
						.Select(row => new KnownOccurrence(row.knownMailbox, row.ProviderOccurrenceId)),
				],
				mailboxes
			),
			ct
		);

		foreach (var mailbox in mailboxes)
		{
			var exactOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);
			var heuristicOccurrenceIds = new HashSet<string>(StringComparer.Ordinal);
			foreach (var dto in candidatesFound)
			{
				var occurrence = dto.Occurrences.FirstOrDefault(candidate =>
					candidate.ProviderMailboxId == mailbox.ProviderMailboxId
				);
				if (occurrence is null)
				{
					continue;
				}

				if (MatchesExactly(message, known, dto))
				{
					exactOccurrenceIds.Add(occurrence.ProviderOccurrenceId);
				}
				else if (MatchesHeuristically(message, dto))
				{
					heuristicOccurrenceIds.Add(occurrence.ProviderOccurrenceId);
				}
			}

			var candidates = exactOccurrenceIds.Count > 0
				? exactOccurrenceIds
				: heuristicOccurrenceIds;
			if (candidates.Count == 1)
			{
				locations[mailbox.Id] = candidates.Single();
			}
			else if (candidates.Count > 1)
			{
				isIndeterminate = true;
				logger.LogWarning(
					"Mutation recovery for message {MessageId} found {Count} indistinguishable candidates in mailbox {MailboxId}; no provider occurrence was adopted.",
					messageId,
					candidates.Count,
					mailbox.Id
				);
			}
		}

		var existsWithoutLocation = locations.Count == 0
			&& candidatesFound.Any(dto => MatchesExactly(message, known, dto) || MatchesHeuristically(message, dto));
		return new MutationObservation(locations, isIndeterminate, existsWithoutLocation);
	}

	/// <param name="ExistsWithoutLocation">
	/// The provider still has the message but in none of the account's folders — a Gmail message with
	/// no labels (archived mail). It is not gone, so a permanent delete cannot count as applied.
	/// </param>
	internal sealed record MutationObservation(
		IReadOnlyDictionary<Guid, string> Locations,
		bool IsIndeterminate,
		bool ExistsWithoutLocation = false
	);

	private static bool MatchesExactly(
		Message message,
		IReadOnlyDictionary<(string ProviderMailboxId, string ProviderOccurrenceId), bool> known,
		MessageDto dto
	) =>
		(message.ProviderStableId is not null && message.ProviderStableId == dto.ProviderStableId)
		|| dto.Occurrences.Any(occurrence =>
			known.ContainsKey((occurrence.ProviderMailboxId, occurrence.ProviderOccurrenceId))
		);

	private static bool MatchesHeuristically(Message message, MessageDto dto) =>
		message.MessageIdHeader is not null
		&& message.MessageIdHeader == dto.MessageIdHeader
		&& message.ReceivedAt == dto.ReceivedAt;

	private async Task<bool> SynchroniseLocationsAsync(Guid messageId, IReadOnlyDictionary<Guid, string> locations, CancellationToken ct)
	{
		var existing = await context.MessageMailboxes.Where(o => o.MessageId == messageId).ToListAsync(ct);
		var vanished = existing.Where(o => !locations.ContainsKey(o.MailboxId)).ToList();
		// The same snapshot movement the executor makes when it settles an outcome itself, so crash
		// recovery shows the count the normal path would have.
		var unread = await UnreadSnapshot.IsUnreadAsync(context, messageId, ct);
		foreach (var occurrence in vanished)
		{
			context.MessageMailboxes.Remove(occurrence);
			if (unread)
			{
				await UnreadSnapshot.AdjustAsync(context, occurrence.MailboxId, -1, ct);
			}
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
				if (unread)
				{
					await UnreadSnapshot.AdjustAsync(context, mailboxId, 1, ct);
				}

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
