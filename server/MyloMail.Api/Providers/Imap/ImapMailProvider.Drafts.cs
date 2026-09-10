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

		// The revision binds the UID to this Drafts folder incarnation. A bare legacy UID
		// is never accepted as a fence; re-resolve this application's stable Message-ID
		// inside the current incarnation before attempting an update.
		if (expectedRevision is not null && !TryParseDraftRevision(expectedRevision, out _, out _))
		{
			var stableMessageId = draft.StableMessageId
				?? throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
			var matches = await drafts.SearchAsync(SearchQuery.HeaderContains("Message-ID", stableMessageId), ct);
			var legacyUid = matches.SingleOrDefault();
			if (!legacyUid.IsValid)
			{
				throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
			}
			expectedRevision = DraftRevision(drafts.UidValidity, legacyUid);
		}
		if (expectedRevision is not null)
		{
			if (!TryParseDraftRevision(expectedRevision, out var uidValidity, out var expectedUid)
				|| uidValidity != drafts.UidValidity)
			{
				throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
			}

			var existing = await drafts.SearchAsync(SearchQuery.Uids([expectedUid]), ct);
			if (existing.Count == 0)
			{
				throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
			}
		}

		var message = Compose(draft, draft.StableMessageId ?? throw new InvalidOperationException("Draft push lacks its stable Message-ID."));
		var appended =
			await drafts.AppendAsync(message, MessageFlags.Draft | MessageFlags.Seen, ct)
			?? throw new InvalidOperationException("The server did not report the appended draft's id.");

		// The previous copy is removed only after the new one is safely stored. The reverse
		// order would lose the draft entirely if the append failed.
		if (expectedRevision is not null)
		{
			_ = TryParseDraftRevision(expectedRevision, out _, out var expectedUidForDelete);
			await drafts.AddFlagsAsync([expectedUidForDelete], MessageFlags.Deleted, true, ct);
			await drafts.ExpungeAsync(ct);
		}

		// The provider id remains the UID because fetch/delete address the current folder; the
		// revision additionally names the UIDVALIDITY incarnation used for conflict detection.
		var appendedUid = appended.Id.ToString();
		return new DraftResult(appendedUid, DraftRevision(drafts.UidValidity, new UniqueId(appended.Id)));
	}

	public async Task<DraftResult?> FindDraftAsync(
		Account account,
		string stableMessageId,
		CancellationToken ct,
		int? maximumBytes = null
	)
	{
		using var client = await ConnectAsync(ct);
		var drafts = client.GetFolder(SpecialFolder.Drafts);
		if (drafts is null)
		{
			return null;
		}
		await drafts.OpenAsync(FolderAccess.ReadOnly, ct);
		var matches = await drafts.SearchAsync(SearchQuery.HeaderContains("Message-ID", stableMessageId), ct);
		var uid = matches.SingleOrDefault();
		return uid.IsValid ? new DraftResult(uid.ToString(), DraftRevision(drafts.UidValidity, uid), uid.ToString()) : null;
	}

	public Task<DraftResult?> FindDraftByMessageIdAsync(Account account, string providerMessageId, CancellationToken ct) =>
		FindDraftAsync(account, providerMessageId, ct);

	public async Task DeleteDraftAsync(
		Account account,
		string providerDraftId,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		using var client = await ConnectAsync(ct);
		var drafts = client.GetFolder(SpecialFolder.Drafts);
		if (drafts is null)
		{
			return;
		}

		await drafts.OpenAsync(FolderAccess.ReadWrite, ct);
		UniqueId uid;
		if (expectedRevision is not null)
		{
			if (!TryParseDraftRevision(expectedRevision, out var uidValidity, out uid)
				|| uidValidity != drafts.UidValidity
				|| uid.Id.ToString() != providerDraftId)
			{
				throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
			}
		}
		else
		{
			uid = ToUids(providerDraftId).SingleOrDefault();
		}
		if (!uid.IsValid)
		{
			return;
		}
		var existing = await drafts.SearchAsync(SearchQuery.Uids([uid]), ct);
		if (existing.Count == 0)
		{
			// The selected UID is absent in the same UIDVALIDITY incarnation: a prior
			// deletion may have completed before its local result committed.
			return;
		}

		await drafts.AddFlagsAsync([uid], MessageFlags.Deleted, true, ct);
		await drafts.ExpungeAsync(ct);
	}

	private static IList<UniqueId> ToUids(string providerDraftId) =>
		UniqueId.TryParse(providerDraftId, out var uid) ? [uid] : [];

	internal static string DraftRevision(uint uidValidity, UniqueId uid) => $"{uidValidity}:{uid.Id}";

	internal static bool TryParseDraftRevision(string revision, out uint uidValidity, out UniqueId uid)
	{
		uidValidity = default;
		uid = UniqueId.Invalid;
		var separator = revision.IndexOf(':');
		if (separator <= 0
			|| !uint.TryParse(revision[..separator], out uidValidity)
			|| !uint.TryParse(revision[(separator + 1)..], out var uidValue))
		{
			return false;
		}

		uid = new UniqueId(uidValue);
		return uid.IsValid;
	}
}
