using MailKit;
using MailKit.Net.Imap;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Folder lifecycle (§2).
/// </summary>
/// <remarks>
/// IMAP hierarchy is a server-specific string convention, so every operation here works from
/// the server's own delimiter and namespace rather than assembling a path. Assuming "/" is
/// how a client breaks on the servers that use "." — which the CondStore tier of the local
/// matrix uses precisely to catch this.
/// </remarks>
public sealed partial class ImapMailProvider
{
	public async Task<MailboxDto> CreateMailboxAsync(
		Account account,
		string name,
		Mailbox? parent,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		// Parenthesised so the null check covers both branches: the namespace root can be
		// absent too, on a server that reports no personal namespace.
		var root =
			(
				parent is null
					? client.GetFolder(client.PersonalNamespaces[0])
					: await client.GetFolderAsync(mailboxes.ProviderMailboxId(parent.Id), ct)
			) ?? throw new InvalidOperationException("The parent folder does not exist on the server.");

		var created =
			await root.CreateAsync(name, isMessageFolder: true, ct)
			?? throw new InvalidOperationException("The server accepted the folder but did not return it.");

		var separator = created.DirectorySeparator;

		return new MailboxDto
		{
			ProviderMailboxId = created.FullName,
			Name = created.Name,
			ParentProviderMailboxId = parent is null ? null : mailboxes.ProviderMailboxId(parent.Id),
			IsSubscribed = created.IsSubscribed,
			ImapMetadata = new ImapMailboxMetadataDto(
				created.FullName,
				separator.ToString(),
				client.PersonalNamespaces[0].Path is { Length: > 0 } prefix ? prefix : null
			),
		};
	}

	public async Task<MailboxDto> RenameMailboxAsync(
		Account account,
		Mailbox mailbox,
		string newName,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await client.GetFolderAsync(mailboxes.ProviderMailboxId(mailbox.Id), ct);

		// Renamed in place: the parent is unchanged, so the server keeps the folder where it
		// is and only the leaf name differs. Moving is a separate operation on purpose. A
		// top-level folder has no parent folder object, so the namespace root stands in.
		var parent = folder.ParentFolder ?? client.GetFolder(client.PersonalNamespaces[0]);
		await folder.RenameAsync(parent, newName, ct);

		// MailKit updates the folder object in place, so this is the server's new full name.
		return Describe(client, folder);
	}

	public async Task<MailboxDto> MoveMailboxAsync(
		Account account,
		Mailbox mailbox,
		Mailbox? newParent,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var folder = await client.GetFolderAsync(mailboxes.ProviderMailboxId(mailbox.Id), ct);
		var destination =
			(
				newParent is null
					? client.GetFolder(client.PersonalNamespaces[0])
					: await client.GetFolderAsync(mailboxes.ProviderMailboxId(newParent.Id), ct)
			)
			?? throw new InvalidOperationException("The destination folder does not exist on the server.");

		// IMAP has no move: a rename to a different parent is the move, and the folder's
		// full name changes as a result — which is why local identity is a Guid and not a path.
		await folder.RenameAsync(destination, folder.Name, ct);
		return Describe(client, folder);
	}

	/// <summary>The folder as the server now names it.</summary>
	private static MailboxDto Describe(ImapClient client, IMailFolder folder) =>
		new()
		{
			ProviderMailboxId = folder.FullName,
			Name = folder.Name,
			ParentProviderMailboxId =
				folder.ParentFolder is { FullName: { Length: > 0 } parentName } ? parentName : null,
			IsSubscribed = folder.IsSubscribed,
			ImapMetadata = new ImapMailboxMetadataDto(
				folder.FullName,
				folder.DirectorySeparator.ToString(),
				client.PersonalNamespaces[0].Path is { Length: > 0 } prefix ? prefix : null
			),
		};

	/// <summary>
	/// Deletes a folder, and with it every message in it.
	/// </summary>
	/// <remarks>
	/// Destructive in a way Gmail's label deletion is not, which is exactly the divergence
	/// <see cref="ProviderCapabilities.DeletingMailboxDeletesMessages"/> exists to surface: the
	/// caller states the real outcome to the user rather than a uniform one (§2).
	/// </remarks>
	public async Task DeleteMailboxAsync(Account account, Mailbox mailbox, CancellationToken ct)
	{
		using var client = await ConnectAsync(ct);
		var folder = await client.GetFolderAsync(mailboxes.ProviderMailboxId(mailbox.Id), ct);

		// Subscriptions outlive the folder on some servers, leaving a ghost in LIST output.
		if (folder.IsSubscribed)
		{
			await folder.UnsubscribeAsync(ct);
		}

		await folder.DeleteAsync(ct);
	}
}
