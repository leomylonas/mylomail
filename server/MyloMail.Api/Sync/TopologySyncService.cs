using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
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

		var byProviderId = existing
			.Where(m => m.ProviderMailboxId is not null)
			.ToDictionary(m => m.ProviderMailboxId!, m => m);

		var seen = new HashSet<string>(StringComparer.Ordinal);
		var added = 0;
		var updated = 0;

		foreach (var dto in reported)
		{
			seen.Add(dto.ProviderMailboxId);

			if (byProviderId.TryGetValue(dto.ProviderMailboxId, out var mailbox))
			{
				Update(mailbox, dto);
				updated++;
			}
			else
			{
				mailbox = Create(account, dto);
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

		var removed = await RemoveVanishedAsync(existing, seen, ct);

		var state = await context.MailboxTopologySyncStates.FirstOrDefaultAsync(s => s.AccountId == account.Id, ct);
		if (state is null)
		{
			state = new MailboxTopologySyncState { AccountId = account.Id };
			context.MailboxTopologySyncStates.Add(state);
		}

		state.LastReconciledAt = clock.GetUtcNow();
		state.LastError = null;

		await context.SaveChangesAsync(ct);

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

		return new TopologyChange(added, updated, removed);
	}

	/// <summary>
	/// A mailbox the provider no longer reports is removed along with its memberships.
	/// </summary>
	/// <remarks>
	/// Synthesised hierarchy nodes are kept: they have no provider object to be reported, so
	/// absence from the provider's list says nothing about them.
	/// </remarks>
	private async Task<int> RemoveVanishedAsync(
		List<Mailbox> existing,
		HashSet<string> seen,
		CancellationToken ct
	)
	{
		var vanished = existing
			.Where(m => m.ProviderMailboxId is not null && !seen.Contains(m.ProviderMailboxId))
			.ToList();

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

		return vanished.Count;
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
