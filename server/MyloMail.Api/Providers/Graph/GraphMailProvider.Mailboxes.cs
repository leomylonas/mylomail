using Microsoft.Graph.Me.MailFolders.Item.Move;
using Microsoft.Graph.Models;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Folder lifecycle (§2). Unlike Gmail, Graph mail folders are genuinely hierarchical —
/// <c>parentFolderId</c> is a real server-side reference, not a naming convention — so this is
/// a direct translation of each operation with no local path reconstruction needed.
/// </summary>
public sealed partial class GraphMailProvider
{
	public async Task<MailboxDto> CreateMailboxAsync(
		Account account,
		string name,
		DomainMailbox? parent,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var folder = new MailFolder { DisplayName = name };
		var created =
			parent is null
				? await ThrottleAwareAsync(() => client.Me.MailFolders.PostAsync(folder, cancellationToken: ct))
				: await ThrottleAwareAsync(
					() => client.Me.MailFolders[ProviderMailboxId(parent)].ChildFolders.PostAsync(
						folder,
						cancellationToken: ct
					)
				);
		return ToDto(created ?? throw new InvalidOperationException("Graph did not return the created folder."));
	}

	public async Task<MailboxDto> RenameMailboxAsync(
		Account account,
		DomainMailbox mailbox,
		string newName,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var updated = await ThrottleAwareAsync(
			() => client.Me.MailFolders[ProviderMailboxId(mailbox)].PatchAsync(
				new MailFolder { DisplayName = newName },
				cancellationToken: ct
			)
		);
		return ToDto(updated ?? throw new InvalidOperationException("Graph did not return the renamed folder."));
	}

	public async Task<MailboxDto> MoveMailboxAsync(
		Account account,
		DomainMailbox mailbox,
		DomainMailbox? newParent,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		// "msgfolderroot" is Graph's well-known id for the mailbox root — accepted anywhere a
		// folder id is, the same way Gmail's well-known label names are.
		var destinationId = newParent is null ? "msgfolderroot" : ProviderMailboxId(newParent);
		var moved = await ThrottleAwareAsync(
			() => client.Me.MailFolders[ProviderMailboxId(mailbox)].Move.PostAsync(
				new MovePostRequestBody { DestinationId = destinationId },
				cancellationToken: ct
			)
		);
		return ToDto(moved ?? throw new InvalidOperationException("Graph did not return the moved folder."));
	}

	public async Task DeleteMailboxAsync(Account account, DomainMailbox mailbox, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		await ThrottleAwareAsync(() => client.Me.MailFolders[ProviderMailboxId(mailbox)].DeleteAsync(null, ct));
	}

	private static MailboxDto ToDto(MailFolder folder) =>
		new()
		{
			ProviderMailboxId = folder.Id ?? throw new InvalidOperationException("Graph did not return a folder id."),
			Name = folder.DisplayName ?? string.Empty,
			ParentProviderMailboxId = folder.ParentFolderId,
			IsSubscribed = true,
			TotalCount = folder.TotalItemCount,
			UnreadCount = folder.UnreadItemCount,
		};
}
