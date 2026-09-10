using Google;
using Google.Apis.Gmail.v1;
using MimeKit;
using MyloMail.Api.Content;
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
			Raw = ToBase64Url(Compose(draft, draft.StableMessageId ?? throw new InvalidOperationException("Draft push lacks its stable Message-ID."))),
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
		return DraftResultOf(updated, draft.ProviderDraftId, draft.ProviderMessageId);
	}

	public async Task<DraftResult?> FindDraftAsync(
		Account account,
		string stableMessageId,
		CancellationToken ct,
		int? maximumBytes = null
	)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.Users.Drafts.List(UserId);
		var matches = new List<DraftResult>();
		var pages = 0;
		do
		{
			if (++pages > 256)
			{
				throw new InvalidOperationException("Gmail draft inventory exceeds the safety limit.");
			}
			var page = await request.ExecuteThrottleAwareAsync(ct);
			foreach (var candidate in page.Drafts ?? [])
			{
				if (candidate.Id is null || candidate.Message?.Id is null)
				{
					continue;
				}
				var messageRequest = service.Users.Messages.Get(UserId, candidate.Message.Id);
				messageRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Raw;
				var message = await messageRequest.ExecuteThrottleAwareAsync(ct);
				if (message.Raw is null || message.HistoryId is null)
				{
					continue;
				}
				using var raw = new MemoryStream(FromBase64Url(message.Raw, maximumBytes));
				var mime = MimeMessage.Load(raw);
				MimeStructureValidator.Validate(mime);
				if (string.Equals(mime.Headers[HeaderId.MessageId], stableMessageId, StringComparison.OrdinalIgnoreCase))
				{
					matches.Add(new DraftResult(candidate.Id, message.HistoryId.ToString(), candidate.Message.Id));
				}
			}
			request.PageToken = page.NextPageToken;
		}
		while (request.PageToken is not null);

		return matches.Count switch
		{
			0 => null,
			1 => matches[0],
			_ => throw new InvalidOperationException("More than one Gmail draft has the same stable Message-ID."),
		};
	}

	public async Task<DraftResult?> FindDraftByMessageIdAsync(Account account, string providerMessageId, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		var request = service.Users.Drafts.List(UserId);
		do
		{
			var page = await request.ExecuteThrottleAwareAsync(ct);
			var candidate = (page.Drafts ?? []).SingleOrDefault(d => d.Message?.Id == providerMessageId);
			if (candidate?.Id is not null)
			{
				var resolved = await service.Users.Drafts.Get(UserId, candidate.Id).ExecuteThrottleAwareAsync(ct);
				return new DraftResult(
					candidate.Id,
					resolved.Message?.HistoryId?.ToString(),
					resolved.Message?.Id ?? providerMessageId
				);
			}
			request.PageToken = page.NextPageToken;
		}
		while (request.PageToken is not null);
		return null;
	}

	public async Task DeleteDraftAsync(Account account, string providerDraftId, string? expectedRevision, CancellationToken ct)
	{
		var service = await ServiceAsync(account, ct);
		if (expectedRevision is not null)
		{
			try
			{
				var existing = await service.Users.Drafts.Get(UserId, providerDraftId).ExecuteThrottleAwareAsync(ct);
				if (existing.Message?.HistoryId?.ToString() != expectedRevision)
				{
					throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
				}
			}
			catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
			{
				return;
			}
		}

		try
		{
			await service.Users.Drafts.Delete(UserId, providerDraftId).ExecuteThrottleAwareAsync(ct);
		}
		catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
		{
			// Already gone — deleting it again is not a failure.
		}
	}

	private static DraftResult DraftResultOf(
		GmailDraft draft,
		string? fallbackId = null,
		string? fallbackMessageId = null
	) => new(
		draft.Id ?? fallbackId ?? throw new InvalidOperationException("Gmail did not return a draft id."),
		draft.Message?.HistoryId?.ToString(),
		draft.Message?.Id ?? fallbackMessageId ?? throw new InvalidOperationException("Gmail did not return a draft message id.")
	);
}
