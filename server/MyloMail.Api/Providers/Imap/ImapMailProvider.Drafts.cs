using MailKit;
using MailKit.Search;
using MimeKit;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;

namespace MyloMail.Api.Providers.Imap;

/// <summary>
/// Server-side drafts (§1, §15).
/// </summary>
/// <remarks>
/// IMAP has no draft API. A draft is a message in the Drafts folder with the <c>\Draft</c>
/// flag, and "updating" one is an append of the new version followed by an expunge of the
/// old — there is no edit. The UID therefore changes on every save, which is why
/// <c>ProviderDraftId</c> is stored and re-read rather than assumed stable.
/// </remarks>
public sealed partial class ImapMailProvider
{
	public async Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var drafts =
			client.GetFolder(SpecialFolder.Drafts)
			?? throw new InvalidOperationException("This account has no Drafts folder.");

		await drafts.OpenAsync(FolderAccess.ReadWrite, ct);

		// Checked before anything is written. On IMAP the revision is the UID of the copy this
		// edit was based on, so its absence means another client replaced or deleted the draft
		// since — detect, never merge (§1).
		if (expectedRevision is not null)
		{
			var existing = await drafts.SearchAsync(SearchQuery.Uids(ToUids(expectedRevision)), ct);
			if (existing.Count == 0)
			{
				throw new ProviderConflictException(
					"This draft was changed somewhere else since it was opened."
				);
			}
		}

		var message = Compose(draft, $"<{Guid.NewGuid():N}@mylomail.local>");
		var appended =
			await drafts.AppendAsync(message, MessageFlags.Draft | MessageFlags.Seen, ct)
			?? throw new InvalidOperationException("The server did not report the appended draft's id.");

		// The previous copy is removed only after the new one is safely stored. The reverse
		// order would lose the draft entirely if the append failed.
		if (expectedRevision is not null)
		{
			await drafts.AddFlagsAsync(ToUids(expectedRevision), MessageFlags.Deleted, true, ct);
			await drafts.ExpungeAsync(ct);
		}

		// The UID is both the id and the revision here: a new one is minted by every save, so
		// they cannot disagree.
		var uid = appended.Id.ToString();
		return new DraftResult(uid, uid);
	}

	public async Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct)
	{
		using var client = await ConnectAsync(ct);
		var drafts = client.GetFolder(SpecialFolder.Drafts);
		if (drafts is null)
		{
			return;
		}

		await drafts.OpenAsync(FolderAccess.ReadWrite, ct);
		await drafts.AddFlagsAsync(ToUids(providerDraftId), MessageFlags.Deleted, true, ct);
		await drafts.ExpungeAsync(ct);
	}

	private static IList<UniqueId> ToUids(string providerDraftId) =>
		UniqueId.TryParse(providerDraftId, out var uid) ? [uid] : [];
}
