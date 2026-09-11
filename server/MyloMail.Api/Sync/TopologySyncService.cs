using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.FaultInjection;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// Discovers and reconciles an account's mailboxes (§1, §3).
/// </summary>
/// <remarks>
/// <b>Folder discovery is not a by-product of message sync.</b> Gmail's <c>history.list</c>
/// is a message-history stream and does not report labels created, renamed or deleted
/// elsewhere, so each provider reconciles topology separately and on its own cadence.
/// </remarks>
public sealed class TopologySyncService(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	TimeProvider clock,
	IFaultInjector faults,
	IHubEvents events,
	ILogger<TopologySyncService> logger
)
{
	public async Task<TopologyChange> ReconcileAsync(Account account, CancellationToken ct = default)
	{
		var reported = await providers.For(account).ListMailboxesAsync(account, ct);
		// The metadata comes with them. Without it every existing mailbox looks as though it
		// has none, so reconciliation attaches a second row and the save fails on the unique
		// key — which is every reconciliation after the first on an IMAP account, and so is
		// every folder created, renamed or deleted from the sidebar.
		var existing = await context
			.Mailboxes.Include(m => m.ImapMetadata)
			.Where(m => m.AccountId == account.Id)
			.ToListAsync(ct);
		await using var transaction = await context.Database.BeginTransactionAsync(ct);

		var byProviderId = existing
			.Where(m => m.ProviderMailboxId is not null)
			.ToDictionary(m => m.ProviderMailboxId!, m => m);

		var seen = new HashSet<string>(StringComparer.Ordinal);
		var added = 0;
		var updated = 0;

		foreach (var dto in reported)
		{
			seen.Add(dto.ProviderMailboxId);
			var effectiveDto = account.ProviderType == ProviderType.Gmail
				? dto with { Name = GmailSegments(dto.Name)[^1] }
				: dto;

			if (byProviderId.TryGetValue(dto.ProviderMailboxId, out var mailbox))
			{
				Update(mailbox, effectiveDto);
				updated++;
			}
			else
			{
				mailbox = Create(account, effectiveDto);
				context.Mailboxes.Add(mailbox);
				byProviderId[dto.ProviderMailboxId] = mailbox;
				added++;
			}
		}

		// Parents are linked in a second pass: a provider may report a child before its
		// parent, and the local id of the parent is not known until it has been created.
		foreach (var dto in reported.Where(d => d.ParentProviderMailboxId is not null))
		{
			if (
				byProviderId.TryGetValue(dto.ProviderMailboxId, out var child)
				&& byProviderId.TryGetValue(dto.ParentProviderMailboxId!, out var parent)
			)
			{
				child.ParentId = parent.Id;
			}
		}

		var (removed, orphanedMessageIds) = await RemoveVanishedAsync(existing, seen, ct);
		if (account.ProviderType == ProviderType.Gmail)
		{
			var hierarchy = ReconcileGmailHierarchy(account.Id, reported, existing, byProviderId);
			added += hierarchy.Added;
			removed += hierarchy.Removed;
		}

		var state = await context.MailboxTopologySyncStates.FirstOrDefaultAsync(s => s.AccountId == account.Id, ct);
		var availabilityRecovered = state?.LastError is not null;
		if (state is null)
		{
			state = new MailboxTopologySyncState { AccountId = account.Id };
			context.MailboxTopologySyncStates.Add(state);
		}

		state.LastReconciledAt = clock.GetUtcNow();
		state.LastError = null;

		await context.SaveChangesAsync(ct);
		faults.Reached(FaultPoints.TopologyAfterApplyBeforeCommit);
		await transaction.CommitAsync(ct);

		logger.LogInformation(
			"Topology for account {AccountId}: {Added} added, {Updated} updated, {Removed} removed.",
			account.Id,
			added,
			updated,
			removed
		);

		// Announced after the commit, and only when the tree actually changed: §7 pairs this
		// event with folder discovery, and raising it on every poll would make it useless as
		// a signal.
		if (added > 0 || removed > 0)
		{
			await events.MailboxTreeChangedAsync(account.Id);
		}

		await MessageChangeAnnouncer.AnnounceDeletedAsync(context, events, orphanedMessageIds, ct);
		if (availabilityRecovered)
		{
			await MailboxSummaryDtoFactory.AnnounceAsync(
				context,
				events,
				account.Id,
				mailboxId: null,
				ct
			);
		}

		return new TopologyChange(added, updated, removed);
	}

	public async Task RecordFailureAsync(
		Guid accountId,
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

			var state = await context.MailboxTopologySyncStates.FirstOrDefaultAsync(
				topologyState => topologyState.AccountId == accountId,
				ct
			);
			if (state is null)
			{
				state = new MailboxTopologySyncState { AccountId = accountId };
				context.MailboxTopologySyncStates.Add(state);
			}
			state.LastError = ex.Message;
			faults.Reached(FaultPoints.MailboxHealthAfterApplyBeforeCommit);
			await context.SaveChangesAsync(ct);
			await transaction.CommitAsync(ct);
			recorded = true;
		});
		if (recorded)
		{
			await MailboxSummaryDtoFactory.AnnounceAsync(
				context,
				events,
				accountId,
				mailboxId: null,
				ct
			);
		}
	}

	private (int Added, int Removed) ReconcileGmailHierarchy(
		Guid accountId,
		IReadOnlyList<MailboxDto> reported,
		List<Mailbox> existing,
		IReadOnlyDictionary<string, Mailbox> byProviderId
	)
	{
		var realByPath = reported.ToDictionary(
			dto => dto.Name,
			dto => byProviderId[dto.ProviderMailboxId],
			StringComparer.Ordinal
		);
		var synthetic = existing.Where(mailbox => mailbox.ProviderMailboxId is null).ToList();
		var retained = new HashSet<Guid>();
		var added = 0;

		foreach (var dto in reported.OrderBy(dto => GmailSegments(dto.Name).Length))
		{
			var segments = GmailSegments(dto.Name);
			Mailbox? parent = null;
			var path = string.Empty;
			for (var index = 0; index < segments.Length; index++)
			{
				var segment = segments[index];
				path = path.Length == 0 ? segment : $"{path}/{segment}";
				Mailbox node;
				if (index == segments.Length - 1)
				{
					node = byProviderId[dto.ProviderMailboxId];
				}
				else if (realByPath.TryGetValue(path, out var real))
				{
					node = real;
				}
				else
				{
					node = synthetic.FirstOrDefault(candidate =>
						candidate.ParentId == parent?.Id
							&& string.Equals(candidate.Name, segment, StringComparison.Ordinal)
					) ?? new Mailbox
					{
						Id = Guid.NewGuid(),
						AccountId = accountId,
						Name = segment,
					};
					if (!synthetic.Contains(node))
					{
						context.Mailboxes.Add(node);
						synthetic.Add(node);
						added++;
					}
					retained.Add(node.Id);
				}

				node.Name = segment;
				node.ParentId = parent?.Id;
				parent = node;
			}
		}

		var stale = synthetic.Where(node => !retained.Contains(node.Id)).ToList();
		context.Mailboxes.RemoveRange(stale);
		return (added, stale.Count);
	}

	private static string[] GmailSegments(string name)
	{
		var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
		return segments.Length == 0 ? [name] : segments;
	}

	/// <summary>
	/// A mailbox the provider no longer reports is removed along with its memberships.
	/// </summary>
	/// <remarks>
	/// Gmail synthetic hierarchy nodes are handled separately: the complete reported label
	/// set proves exactly which intermediate paths still exist.
	/// </remarks>
	private async Task<(int Removed, IReadOnlyList<Guid> OrphanedMessageIds)> RemoveVanishedAsync(
		List<Mailbox> existing,
		HashSet<string> seen,
		CancellationToken ct
	)
	{
		var vanished = existing
			.Where(m => m.ProviderMailboxId is not null && !seen.Contains(m.ProviderMailboxId))
			.ToList();
		if (vanished.Count == 0)
		{
			return (0, []);
		}

		// Memberships cascade with the mailbox row, so the messages that lived only here are
		// about to become invisible. Captured before the delete, because afterwards there is
		// nothing left to join against (§7).
		var vanishedIds = vanished.Select(mailbox => mailbox.Id).ToList();
		var orphaned = await context
			.MessageMailboxes.Where(occurrence => vanishedIds.Contains(occurrence.MailboxId))
			.Select(occurrence => occurrence.MessageId)
			.Distinct()
			.ToListAsync(ct);

		foreach (var mailbox in vanished)
		{
			// Children are re-parented to the vanished mailbox's parent rather than being
			// cascaded away: the local FK deliberately restricts, so that removing a folder
			// is an explicit decision rather than a silent subtree deletion.
			var children = await context.Mailboxes.Where(m => m.ParentId == mailbox.Id).ToListAsync(ct);
			foreach (var child in children)
			{
				child.ParentId = mailbox.ParentId;
			}

			context.Mailboxes.Remove(mailbox);
		}

		return (vanished.Count, orphaned);
	}

	/// <summary>
	/// Records that a mailbox has been replaced, so that work issued under the old one is
	/// discarded.
	/// </summary>
	/// <remarks>
	/// A folder deleted and recreated with the same name — an ordinary thing on IMAP, and a
	/// replaced folder on Graph — produces a mailbox that is new despite looking identical.
	/// Without the generation, a page still in flight from the previous incarnation would
	/// resurrect or mutate state belonging to a mailbox that no longer exists. This is the
	/// topology-domain equivalent of §6's staleness protection.
	/// </remarks>
	public async Task<int> BumpGenerationAsync(Guid mailboxId, CancellationToken ct = default)
	{
		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == mailboxId, ct);
		mailbox.TopologyGeneration++;

		// Coverage restarts: what was fetched belonged to the previous incarnation.
		var coverage = await context.MailboxCoverageStates.FirstOrDefaultAsync(c => c.MailboxId == mailboxId, ct);
		if (coverage is not null)
		{
			coverage.Status = CoverageStatus.NotStarted;
			coverage.ResumeToken = null;
			coverage.MessagesFetched = 0;
		}

		await context.SaveChangesAsync(ct);
		return mailbox.TopologyGeneration;
	}

	private static Mailbox Create(Account account, MailboxDto dto) =>
		new()
		{
			Id = Guid.NewGuid(),
			AccountId = account.Id,
			ProviderMailboxId = dto.ProviderMailboxId,
			Name = dto.Name,
			SpecialUse = dto.SpecialUse,
			IsSubscribed = dto.IsSubscribed,
			ProviderTotalCount = dto.TotalCount,
			ProviderUnreadCount = dto.UnreadCount,
			ImapMetadata = ToMetadata(dto),
		};

	/// <summary>
	/// Provider-reported counts are refreshed on every reconciliation, because they are what
	/// the sidebar displays: a locally computed count is wrong under bounded sync (§1).
	/// </summary>
	private static void Update(Mailbox mailbox, MailboxDto dto)
	{
		mailbox.Name = dto.Name;
		mailbox.SpecialUse = dto.SpecialUse;
		mailbox.IsSubscribed = dto.IsSubscribed;
		mailbox.ProviderTotalCount = dto.TotalCount;
		mailbox.ProviderUnreadCount = dto.UnreadCount;

		if (ToMetadata(dto) is ImapMailboxMetadata metadata)
		{
			if (mailbox.ImapMetadata is null)
			{
				mailbox.ImapMetadata = metadata;
			}
			else
			{
				mailbox.ImapMetadata.FullName = metadata.FullName;
				mailbox.ImapMetadata.HierarchyDelimiter = metadata.HierarchyDelimiter;
				mailbox.ImapMetadata.NamespacePrefix = metadata.NamespacePrefix;
			}
		}
	}

	private static ImapMailboxMetadata? ToMetadata(MailboxDto dto) =>
		dto.ImapMetadata is null
			? null
			: new ImapMailboxMetadata
			{
				FullName = dto.ImapMetadata.FullName,
				// The delimiter is server-declared and varies; it is carried as a string over
				// the wire because char has no TypeScript equivalent.
				HierarchyDelimiter = dto.ImapMetadata.HierarchyDelimiter.FirstOrDefault(),
				NamespacePrefix = dto.ImapMetadata.NamespacePrefix,
			};
}

public sealed record TopologyChange(int Added, int Updated, int Removed);
