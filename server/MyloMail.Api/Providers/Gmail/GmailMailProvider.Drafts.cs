using Google;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using GmailDraft = Google.Apis.Gmail.v1.Data.Draft;
using GmailMessage = Google.Apis.Gmail.v1.Data.Message;

namespace MyloMail.Api.Providers.Gmail;

/// <summary>
/// Server-side drafts (§1, §15).
/// </summary>
/// <remarks>
/// Gmail exposes no HTTP ETag precondition on a draft update, unlike Graph's <c>@odata.etag</c>
/// — so the revision this provider hands back and re-checks is the underlying message's
/// <c>historyId</c> (monotonic per observation), read back before every overwrite (§1).
/// </remarks>
public sealed partial class GmailMailProvider
{
	public async Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		var service = await ServiceAsync(account, ct);
		var message = new GmailMessage
		{
			Raw = ToBase64Url(Compose(draft, $"<{Guid.NewGuid():N}@mylomail.local>")),
		};

		if (expectedRevision is null || draft.ProviderDraftId is null)
		{
			var created = await service.Users.Drafts.Create(new GmailDraft { Message = message }, UserId)
				.ExecuteThrottleAwareAsync(ct);
			return DraftResultOf(created);
		}

		GmailDraft existing;
		try
		{
			existing = await service.Users.Drafts.Get(UserId, draft.ProviderDraftId).ExecuteThrottleAwareAsync(ct);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
		{
			throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
		}

		if (existing.Message?.HistoryId?.ToString() != expectedRevision)
		{
			throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
		}

		var updated = await service.Users.Drafts
			.Update(new GmailDraft { Message = message }, UserId, draft.ProviderDraftId)
			.ExecuteThrottleAwareAsync(ct);
		return DraftResultOf(updated, draft.ProviderDraftId);
	}

	public async Task DeleteDraftAsync(Account account, string providerDraftId, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		try
		{
			await service.Users.Drafts.Delete(UserId, providerDraftId).ExecuteThrottleAwareAsync(ct);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
		{
			// Already gone — deleting it again is not a failure.
		}
	}

	private static DraftResult DraftResultOf(GmailDraft draft, string? fallbackId = null) =>
		new(draft.Id ?? fallbackId ?? throw new InvalidOperationException("Gmail did not return a draft id."), draft.Message?.HistoryId?.ToString());
}
