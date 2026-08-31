using Microsoft.EntityFrameworkCore;
using MyloMail.Api.Domain;
using MyloMail.Api.Hubs;
using MyloMail.Api.Persistence;
using MyloMail.Api.Providers;

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

		await providers.For(account).RenameMailboxAsync(account, mailbox, newName, ct);

		// A rename changes the folder's provider identity on IMAP, where the id is its full
		// path. Reconciliation is what re-establishes the mapping; the local Guid is unchanged
		// throughout, which is why queued work referring to this mailbox survives (§6).
		await ReconcileAsync(account, ct);
	}

	public async Task MoveAsync(Guid mailboxId, Guid? newParentId, CancellationToken ct = default)
	{
		var (account, mailbox) = await ResolveAsync(mailboxId, ct);
		var parent = newParentId is Guid id
			? await context.Mailboxes.FirstOrDefaultAsync(m => m.Id == id, ct)
			: null;

		await providers.For(account).MoveMailboxAsync(account, mailbox, parent, ct);
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

	private async Task<(Account Account, Mailbox Mailbox)> ResolveAsync(
		Guid mailboxId,
		CancellationToken ct
	)
	{
		var mailbox = await context.Mailboxes.FirstAsync(m => m.Id == mailboxId, ct);
		var account = await context.Accounts.FirstAsync(a => a.Id == mailbox.AccountId, ct);
		return (account, mailbox);
	}

	private async Task ReconcileAsync(Account account, CancellationToken ct)
	{
		await topology.ReconcileAsync(account, ct);
		await events.MailboxTreeChangedAsync(account.Id);
	}
}
