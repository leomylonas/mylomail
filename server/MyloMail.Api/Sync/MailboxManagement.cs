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

		await providers.For(account).CreateMailboxAsync(account, name, parent, ct);
		await ReconcileAsync(account, ct);
	}

	public async Task RenameAsync(Guid mailboxId, string newName, CancellationToken ct = default)
	{
		var (account, mailbox) = await ResolveAsync(mailboxId, ct);
		var renamed = await providers.For(account).RenameMailboxAsync(account, mailbox, newName, ct);

		await AdoptAsync(mailbox, renamed, ct);
		await ReconcileAsync(account, ct);
	}

	public async Task MoveAsync(Guid mailboxId, Guid? newParentId, CancellationToken ct = default)
	{
		var (account, mailbox) = await ResolveAsync(mailboxId, ct);
		var parent = newParentId is Guid id
			? await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == id, ct)
			: null;

		var moved = await providers.For(account).MoveMailboxAsync(account, mailbox, parent, ct);

		await AdoptAsync(mailbox, moved, ct);
		await ReconcileAsync(account, ct);
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

		await provider.DeleteMailboxAsync(account, mailbox, ct);

		// The generation is bumped before reconciliation removes the row, so any page still in
		// flight for this mailbox is discarded rather than resurrecting it (§1).
		await topology.BumpGenerationAsync(mailboxId, ct);
		await ReconcileAsync(account, ct);

		logger.LogInformation("Mailbox {MailboxId} deleted.", mailboxId);
		return provider.Capabilities.DeletingMailboxDeletesMessages;
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
