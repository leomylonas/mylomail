using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
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
			var policy = MutationRecoveryPolicyResolver.For(attempt.OperationKind, providers.For(account).Capabilities);
			if (policy == MutationRecoveryPolicy.RetrySafe)
			{
				await RequeueAsync(attempt, ct);
				settled++;
				continue;
			}

			var observed = new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>();
			foreach (var membership in attempt.Items)
			{
				var item = items[membership.MutationItemId];
				observed[item.Id] = await ObserveAsync(account, item.MessageId, ct);
			}

			foreach (var membership in attempt.Items)
			{
				var item = items[membership.MutationItemId];
				var locations = observed[item.Id];
				var applied = await IsAppliedAsync(account, item, locations, ct);
				if (applied)
				{
					await SynchroniseLocationsAsync(item.MessageId, locations, ct);
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
			settled++;
		}

		if (settled > 0)
		{
			logger.LogInformation("Reconciled {Attempts} ambiguous mutation attempts for account {AccountId}.", settled, accountId);
		}
		return settled;
	}

	private async Task RequeueAsync(MutationExecutionAttempt attempt, CancellationToken ct)
	{
		var itemIds = attempt.Items.Select(i => i.MutationItemId).ToList();
		var items = await context.MutationItems.Where(i => itemIds.Contains(i.Id)).ToListAsync(ct);
		foreach (var item in items)
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

	private async Task<bool> IsAppliedAsync(
		Account account,
		MutationItem item,
		IReadOnlyDictionary<Guid, string> locations,
		CancellationToken ct
	)
	{
		switch (item.OperationKind)
		{
			case MutationOperationKind.MoveMessage:
				return item.TargetMailboxId is Guid target && locations.ContainsKey(target);
			case MutationOperationKind.MoveToTrash:
				var trashId = await context.Mailboxes
					.Where(m => m.AccountId == account.Id && m.SpecialUse == SpecialUse.Trash)
					.Select(m => (Guid?)m.Id)
					.FirstOrDefaultAsync(ct);
				return trashId is Guid trash && locations.ContainsKey(trash);
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
		var known = await context.MessageMailboxes.Where(o => o.MessageId == messageId).Select(o => o.ProviderOccurrenceId).ToListAsync(ct);
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

	private static bool Matches(Message message, IReadOnlyCollection<string> known, MessageDto dto) =>
		(message.ProviderStableId is not null && message.ProviderStableId == dto.ProviderStableId)
		|| dto.Occurrences.Any(o => known.Contains(o.ProviderOccurrenceId))
		|| (message.MessageIdHeader is not null
			&& message.MessageIdHeader == dto.MessageIdHeader
			&& message.ReceivedAt == dto.ReceivedAt);

	private async Task SynchroniseLocationsAsync(Guid messageId, IReadOnlyDictionary<Guid, string> locations, CancellationToken ct)
	{
		var existing = await context.MessageMailboxes.Where(o => o.MessageId == messageId).ToListAsync(ct);
		foreach (var occurrence in existing.Where(o => !locations.ContainsKey(o.MailboxId)))
		{
			context.MessageMailboxes.Remove(occurrence);
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
	}
}
