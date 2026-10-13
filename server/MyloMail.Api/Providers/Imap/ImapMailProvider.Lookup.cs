using MailKit;
using MailKit.Search;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

public sealed partial class ImapMailProvider
{
	/// <summary>
	/// A UID is folder-scoped and a move gives the message a new one, so recovery asks each folder
	/// for the <c>Message-ID</c> (a server-side search, not a download) and also reads the UIDs it
	/// last knew, which covers a message with no usable header. Only the matches are fetched.
	/// </summary>
	public async Task<IReadOnlyList<MessageDto>> FindMessageAsync(
		Account account,
		MessageLookup lookup,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var found = new Dictionary<(string Folder, uint Uid), MessageDto>();

		foreach (var mailbox in lookup.Mailboxes)
		{
			var wanted = new List<UniqueId>();
			var folder = await OpenAsync(client, mailbox, FolderAccess.ReadOnly, ct);
			if (lookup.MessageIdHeader is { Length: > 0 } header)
			{
				wanted.AddRange(await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", header), ct));
			}

			foreach (var known in lookup.KnownOccurrences.Where(known => known.Mailbox.Id == mailbox.Id))
			{
				if (uint.TryParse(known.ProviderOccurrenceId, out var uid))
				{
					wanted.Add(new UniqueId(uid));
				}
			}

			wanted = [.. wanted.Distinct()];
			foreach (var dto in await SummariseAsync(folder, wanted, ct))
			{
				var occurrence = dto.Occurrences[0];
				if (uint.TryParse(occurrence.ProviderOccurrenceId, out var uid))
				{
					found[(occurrence.ProviderMailboxId, uid)] = dto;
				}
			}
		}

		await client.DisconnectAsync(true, ct);
		return [.. found.Values];
	}
}
