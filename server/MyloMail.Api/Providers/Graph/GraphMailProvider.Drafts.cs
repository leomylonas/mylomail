using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Providers.Contracts;
using static MyloMail.Api.Providers.Graph.GraphThrottleAwareRequests;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

/// <summary>
/// Server-side drafts (§1, §15). Graph's <c>@odata.etag</c> is the precondition IMAP and Gmail
/// have to synthesise — a draft update sends <c>If-Match</c> with the previously-read etag, and
/// a precondition failure surfaces directly as <see cref="ProviderConflictException"/>.
/// </summary>
public sealed partial class GraphMailProvider
{
	public async Task<DraftResult> CreateOrUpdateDraftAsync(
		Account account,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var message = ToDraftMessage(draft, draft.StableMessageId);

		if (expectedRevision is null || draft.ProviderDraftId is null)
		{
			var created =
				await ThrottleAwareAsync(() => client.Me.Messages.PostAsync(message, cancellationToken: ct))
				?? throw new InvalidOperationException("Graph did not return the created draft.");
			return DraftResultOf(created);
		}

		try
		{
			var updated = await ThrottleAwareAsync(
				() => client.Me.Messages[draft.ProviderDraftId].PatchAsync(
					message,
					configuration => configuration.Headers.Add("If-Match", expectedRevision),
					ct
				)
			);
			return DraftResultOf(updated, draft.ProviderDraftId);
		}
		catch (ApiException ex) when (ex.ResponseStatusCode is 412 or 404)
		{
			throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
		}
	}

	public async Task<DraftResult?> FindDraftAsync(
		Account account,
		string stableMessageId,
		CancellationToken ct,
		int? maximumBytes = null
	)
	{
		var client = await ClientAsync(account, ct);
		var page = await ThrottleAwareAsync(() => client.Me.Messages.GetAsync(
			configuration =>
			{
				configuration.QueryParameters.Filter =
					$"internetMessageId eq '{stableMessageId.Replace("'", "''", StringComparison.Ordinal)}' and isDraft eq true";
				configuration.QueryParameters.Select = ["id", "@odata.etag", "isDraft"];
			},
			ct
		));
		var found = page?.Value?.SingleOrDefault();
		return found?.Id is null ? null : DraftResultOf(found);
	}

	public async Task<DraftResult?> FindDraftByMessageIdAsync(Account account, string providerMessageId, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		var message = await ThrottleAwareAsync(() => client.Me.Messages[providerMessageId].GetAsync(
			configuration => configuration.QueryParameters.Select = ["id", "@odata.etag", "isDraft"],
			ct
		));
		return message?.IsDraft == true ? DraftResultOf(message) : null;
	}

	public async Task DeleteDraftAsync(Account account, string providerDraftId, string? expectedRevision, CancellationToken ct)
	{
		var client = await ClientAsync(account, ct);
		try
		{
			await ThrottleAwareAsync(() => client.Me.Messages[providerDraftId].DeleteAsync(
				configuration =>
				{
					if (expectedRevision is not null)
					{
						configuration.Headers.Add("If-Match", expectedRevision);
					}
				},
				ct
			));
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			// Already gone — deleting it again is not a failure.
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 412)
		{
			throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
		}
	}

	private static GraphMessage ToDraftMessage(Draft draft, string? stableMessageId = null) =>
		new()
		{
			ToRecipients = [.. draft.To.Select(ToRecipient)],
			CcRecipients = [.. draft.Cc.Select(ToRecipient)],
			BccRecipients = [.. draft.Bcc.Select(ToRecipient)],
			Subject = draft.Subject,
			InternetMessageId = stableMessageId,
			Body = new Microsoft.Graph.Models.ItemBody
			{
				ContentType = Microsoft.Graph.Models.BodyType.Html,
				Content = draft.BodyHtml,
			},
		};

	private static DraftResult DraftResultOf(GraphMessage? message, string? fallbackId = null) =>
		new(
			message?.Id ?? fallbackId ?? throw new InvalidOperationException("Graph did not return a draft id."),
			message?.AdditionalData?.TryGetValue("@odata.etag", out var etag) == true ? etag as string : null,
			message?.Id ?? fallbackId
		);
}
