using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Sync;

/// <summary>
/// Folder lifecycle against the provider, followed by topology reconciliation (§2, §3).
/// </summary>
/// <remarks>
/// <para>
/// These are <b>not</b> message mutations and deliberately do not go through the mutation
/// queue: §6 keeps mailbox and account lifecycle in a separate coordination domain, because
/// the message chain is keyed by message and must not become a general-purpose lock manager.
/// </para>
/// <para>
/// Each operation reconciles topology afterwards rather than editing the local row directly.
/// The server decides what a folder is called and where it sits — IMAP renames change a
/// folder's full name, and a create may be adjusted to the server's namespace — so reading
/// the result back is the only way the local model stays true.
/// </para>
/// </remarks>
public sealed class MailboxManagement(
	MyloMailDbContext context,
	IMailProviderFactory providers,
	TopologySyncService topology,
	IHubEvents events,
	ILogger<MailboxManagement> logger
)
{
	public async Task CreateAsync(
		Guid accountId,
		string name,
		Guid? parentId,
		CancellationToken ct = default
	)
	{
		var account = await context.Accounts.FirstAsync(a => a.Id == accountId, ct);
		var parent = parentId is Guid id
			? await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == id, ct)
			: null;

		await RunProviderCallAsync(() => providers.For(account).CreateMailboxAsync(account, name, parent, ct));
		await ReconcileAsync(account, ct);
	}

	public async Task RenameAsync(Guid mailboxId, string newName, CancellationToken ct = default)
	{
		var (account, mailbox) = await ResolveAsync(mailboxId, ct);
		var renamed = await RunProviderCallAsync(
			() => providers.For(account).RenameMailboxAsync(account, mailbox, newName, ct)
		);

		await AdoptAsync(mailbox, renamed, ct);
		await ReconcileAsync(account, ct);
	}

	public async Task MoveAsync(Guid mailboxId, Guid? newParentId, CancellationToken ct = default)
	{
		var (account, mailbox) = await ResolveAsync(mailboxId, ct);

		// The client's own drag UI already guards this, but it is not the only caller a hub
		// method has to assume — a cyclic ParentId chain is a tree with no way back out for
		// every reader that walks it (the sidebar's recursive render included), so it is
		// rejected here rather than trusted to have been rejected already.
		if (newParentId is Guid candidateParentId && await IsDescendantOfAsync(candidateParentId, mailboxId, ct))
		{
			throw new HubException("A folder cannot be moved into itself or one of its own subfolders.");
		}

		var parent = newParentId is Guid id
			? await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == id, ct)
			: null;

		var moved = await RunProviderCallAsync(
			() => providers.For(account).MoveMailboxAsync(account, mailbox, parent, ct)
		);

		await AdoptAsync(mailbox, moved, ct);
		await ReconcileAsync(account, ct);
	}

	/// <summary>Whether <paramref name="candidateId"/> is <paramref name="ancestorId"/> itself or sits beneath it.</summary>
	private async Task<bool> IsDescendantOfAsync(Guid candidateId, Guid ancestorId, CancellationToken ct)
	{
		var current = candidateId;
		while (true)
		{
			if (current == ancestorId)
			{
				return true;
			}

			var parentId = await context
				.Mailboxes.Where(m => m.Id == current)
				.Select(m => m.ParentId)
				.FirstOrDefaultAsync(ct);
			if (parentId is not Guid next)
			{
				return false;
			}

			current = next;
		}
	}

	/// <summary>
	/// Deletes a mailbox, with the consequence the provider actually has.
	/// </summary>
	/// <remarks>
	/// Returns whether the messages went with it, so the caller can say so honestly: deleting
	/// an IMAP folder or a Graph folder destroys its messages, while deleting a Gmail label
	/// leaves them in All Mail. A uniform "are you sure?" would be wrong for one of them (§2).
	/// </remarks>
	public async Task<bool> DeleteAsync(Guid mailboxId, CancellationToken ct = default)
	{
		var (account, mailbox) = await ResolveAsync(mailboxId, ct);
		var provider = providers.For(account);

		await RunProviderCallAsync(() => provider.DeleteMailboxAsync(account, mailbox, ct));

		// The generation is bumped before reconciliation removes the row, so any page still in
		// flight for this mailbox is discarded rather than resurrecting it (§1).
		await topology.BumpGenerationAsync(mailboxId, ct);
		await ReconcileAsync(account, ct);

		logger.LogInformation("Mailbox {MailboxId} deleted.", mailboxId);
		return provider.Capabilities.DeletingMailboxDeletesMessages;
	}

	/// <summary>
	/// Sidebar drag-reorder among siblings (§13 Epic 2). Purely local: no provider supports
	/// arbitrary folder ordering, so this never calls a provider and never goes through
	/// topology reconciliation — there is nothing remote to reconcile against.
	/// </summary>
	public async Task ReorderAsync(
		Guid accountId,
		Guid? parentId,
		IReadOnlyList<Guid> orderedMailboxIds,
		CancellationToken ct = default
	)
	{
		var siblings = await context
			.Mailboxes.Where(m => m.AccountId == accountId && m.ParentId == parentId)
			.ToDictionaryAsync(m => m.Id, ct);

		for (var index = 0; index < orderedMailboxIds.Count; index++)
		{
			if (siblings.TryGetValue(orderedMailboxIds[index], out var mailbox))
			{
				mailbox.LocalSortOrder = index;
			}
		}

		await context.SaveChangesAsync(ct);
		await events.MailboxTreeChangedAsync(accountId);
	}

	/// <summary>
	/// Carries the provider's new identity onto the existing local row, and onto everything
	/// beneath it.
	/// </summary>
	/// <remarks>
	/// Without this the following reconciliation sees the old provider id gone and a new
	/// folder in its place: it deletes the local mailbox and creates another, and the Guid
	/// that mutations, coverage state and cursors refer to is destroyed by a rename — the one
	/// operation §6 says must not invalidate queued work. Descendants are remapped too,
	/// because renaming an IMAP folder renames its whole subtree: their ids are paths that
	/// begin with their ancestor's.
	/// </remarks>
	private async Task AdoptAsync(Mailbox mailbox, MailboxDto renamed, CancellationToken ct)
	{
		var oldId = mailbox.ProviderMailboxId;
		mailbox.ProviderMailboxId = renamed.ProviderMailboxId;
		mailbox.Name = renamed.Name;

		if (mailbox.ImapMetadata is not null)
		{
			mailbox.ImapMetadata.FullName = renamed.ProviderMailboxId;
		}

		if (oldId is not null && renamed.ImapMetadata is { } metadata)
		{
			var delimiter = metadata.HierarchyDelimiter;
			var prefix = oldId + delimiter;

			var descendants = await context
				.Mailboxes.Include(m => m.ImapMetadata)
				.Where(m => m.AccountId == mailbox.AccountId && m.ProviderMailboxId!.StartsWith(prefix))
				.ToListAsync(ct);

			foreach (var descendant in descendants)
			{
				descendant.ProviderMailboxId =
					renamed.ProviderMailboxId + descendant.ProviderMailboxId![oldId.Length..];

				if (descendant.ImapMetadata is not null)
				{
					descendant.ImapMetadata.FullName = descendant.ProviderMailboxId;
				}
			}
		}

		await context.SaveChangesAsync(ct);
	}

	/// <summary>
	/// Surfaces a provider's rejection of a folder-lifecycle call to the caller with its real
	/// message, instead of SignalR's default "An unexpected error occurred" (detailed errors
	/// are off, matching every other hub method). MailboxTree.tsx's error handling exists
	/// specifically to show the user why a create/rename/move/delete was rejected — a duplicate
	/// name, a namespace the server won't accept, a special folder it won't delete — which is
	/// silently defeated unless the failure is rethrown as a <see cref="HubException"/>, the
	/// one exception type SignalR forwards verbatim.
	/// </summary>
	private async Task RunProviderCallAsync(Func<Task> call)
	{
		try
		{
			await call();
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not HubException)
		{
			// Logged before the rethrow: HubException carries only ex.Message to the caller, so
			// this is the last point the original exception (type, stack trace) is still
			// available to distinguish a legitimate provider rejection from a genuine defect.
			logger.LogWarning(ex, "A mailbox provider call was rejected.");
			throw new HubException(ex.Message);
		}
	}

	private async Task<T> RunProviderCallAsync<T>(Func<Task<T>> call)
	{
		try
		{
			return await call();
		}
		catch (Exception ex) when (ex is not OperationCanceledException and not HubException)
		{
			logger.LogWarning(ex, "A mailbox provider call was rejected.");
			throw new HubException(ex.Message);
		}
	}

	private async Task<(Account Account, Mailbox Mailbox)> ResolveAsync(
		Guid mailboxId,
		CancellationToken ct
	)
	{
		var mailbox = await context
			.Mailboxes.Include(m => m.ImapMetadata)
			.FirstAsync(m => m.Id == mailboxId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == mailbox.AccountId, ct);
		return (account, mailbox);
	}

	private async Task ReconcileAsync(Account account, CancellationToken ct)
	{
		await topology.ReconcileAsync(account, ct);
		await events.MailboxTreeChangedAsync(account.Id);
	}
}
