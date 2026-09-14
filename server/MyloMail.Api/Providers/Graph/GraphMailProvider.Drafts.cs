using Microsoft.Graph;
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
		return await CreateOrUpdateDraftWithClientAsync(client, draft, expectedRevision, ct);
	}

	internal static async Task<DraftResult> CreateOrUpdateDraftWithClientAsync(
		GraphServiceClient client,
		Draft draft,
		string? expectedRevision,
		CancellationToken ct
	)
	{
		var message = ToDraftMessage(
			draft,
			draft.StableMessageId,
			includeSmallAttachments: expectedRevision is null || draft.ProviderDraftId is null
		);

		if (expectedRevision is null || draft.ProviderDraftId is null)
		{
			GraphMessage? created = null;
			try
			{
				created =
					await ThrottleAwareAsync(() =>
						client.Me.Messages.PostAsync(message, cancellationToken: ct)
					) ?? throw new InvalidOperationException("Graph did not return the created draft.");
				var draftId = created.Id
					?? throw new InvalidOperationException("Graph's created draft has no id.");
				await UploadDraftAttachmentsAsync(client, draftId, draft.Attachments.Where(
					attachment => attachment.Content.LongLength > InlineAttachmentLimit
				), ct);
				var persisted = await EnsureDraftFromIdentityAsync(
					client,
					draftId,
					draft.FromAddress,
					ct
				);
				return DraftResultOf(persisted, draftId);
			}
			catch (ProviderDraftRejectedException)
			{
				throw;
			}
			catch (Exception ex) when (created?.Id is not null)
			{
				throw new ProviderDraftRejectedException(
					"Microsoft Graph created the draft but could not finish preparing it.",
					DraftResultOf(created),
					ex
				);
			}
		}

		var patchSucceeded = false;
		try
		{
			await ThrottleAwareAsync(
				() => client.Me.Messages[draft.ProviderDraftId].PatchAsync(
					message,
					configuration => configuration.Headers.Add("If-Match", expectedRevision),
					ct
				)
			);
			patchSucceeded = true;
			await ReplaceDraftAttachmentsAsync(client, draft.ProviderDraftId, draft.Attachments, ct);
			var persisted = await EnsureDraftFromIdentityAsync(
				client,
				draft.ProviderDraftId,
				draft.FromAddress,
				ct
			);
			return DraftResultOf(persisted, draft.ProviderDraftId);
		}
		catch (ApiException ex) when (!patchSucceeded && ex.ResponseStatusCode is 412 or 404)
		{
			throw new ProviderConflictException("This draft was changed somewhere else since it was opened.");
		}
		catch (ProviderDraftRejectedException) when (patchSucceeded)
		{
			throw;
		}

		catch (Exception ex) when (patchSucceeded)
		{
			try
			{
				var recovered = await DraftResultAfterPartialUpdateAsync(client, draft.ProviderDraftId, ct);
				throw new ProviderDraftRejectedException(
					"Microsoft Graph saved the draft update but could not finish replacing its attachments.",
					recovered
				);
			}
			catch (ProviderDraftRejectedException)
			{
				throw;
			}
			catch
			{
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
				throw;
			}
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
				configuration.QueryParameters.Select = ["id", "isDraft"];
			},
			ct
		));
		var found = page?.Value?.SingleOrDefault();
		return found?.Id is null ? null : DraftResultOf(found);
	}

	public async Task<DraftResult?> FindDraftByMessageIdAsync(
		Account account,
		string providerMessageId,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		try
		{
			var message = await ThrottleAwareAsync(() =>
				client.Me.Messages[providerMessageId].GetAsync(
					configuration => configuration.QueryParameters.Select = ["id", "isDraft"],
					ct
				)
			);
			return message?.IsDraft == true ? DraftResultOf(message) : null;
		}
		catch (ApiException ex) when (ex.ResponseStatusCode == 404)
		{
			return null;
		}
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

	internal static GraphMessage ToDraftMessage(
		Draft draft,
		string? stableMessageId = null,
		bool includeSmallAttachments = true
	) =>
		new()
		{
			ToRecipients = [.. draft.To.Select(ToRecipient)],
			CcRecipients = [.. draft.Cc.Select(ToRecipient)],
			BccRecipients = [.. draft.Bcc.Select(ToRecipient)],
			From = string.IsNullOrWhiteSpace(draft.FromAddress)
				? null
				: ToRecipient(new Address(null, draft.FromAddress)),
			Subject = draft.Subject,
			InternetMessageId = stableMessageId,
			Body = new Microsoft.Graph.Models.ItemBody
			{
				ContentType = Microsoft.Graph.Models.BodyType.Html,
				Content = draft.BodyHtml,
			},
			Attachments = includeSmallAttachments
				?
				[
					.. draft.Attachments
						.Where(attachment => attachment.Content.LongLength <= InlineAttachmentLimit)
						.Select(ToFileAttachment),
				]
				: null,
		};

	internal static async Task ReplaceDraftAttachmentsAsync(
		GraphServiceClient client,
		string draftId,
		IReadOnlyList<DraftAttachment> attachments,
		CancellationToken ct
	)
	{
		var page = await ThrottleAwareAsync(
			() => client.Me.Messages[draftId].Attachments.GetAsync(cancellationToken: ct)
		);
		while (page is not null)
		{
			foreach (var attachment in page.Value ?? [])
			{
				if (attachment.Id is not null)
				{
					await ThrottleAwareAsync(
						() => client.Me.Messages[draftId].Attachments[attachment.Id].DeleteAsync(
							cancellationToken: ct
						)
					);
				}
			}

			page = page.OdataNextLink is null
				? null
				: await ThrottleAwareAsync(
					() => client.Me.Messages[draftId].Attachments.WithUrl(page.OdataNextLink).GetAsync(
						cancellationToken: ct
					)
				);
		}

		await UploadDraftAttachmentsAsync(client, draftId, attachments, ct);
	}

	internal static async Task UploadDraftAttachmentsAsync(
		GraphServiceClient client,
		string draftId,
		IEnumerable<DraftAttachment> attachments,
		CancellationToken ct
	)
	{
		foreach (var attachment in attachments)
		{
			if (attachment.Content.LongLength <= InlineAttachmentLimit)
			{
				await ThrottleAwareAsync(
					() => client.Me.Messages[draftId].Attachments.PostAsync(
						ToFileAttachment(attachment),
						cancellationToken: ct
					)
				);
			}
			else
			{
				await UploadLargeAttachmentAsync(client, draftId, attachment, ct);
			}
		}
	}

	internal static async Task<GraphMessage> EnsureDraftFromIdentityAsync(
		GraphServiceClient client,
		string draftId,
		string fromAddress,
		CancellationToken ct
	)
	{
		// Attachment mutations advance the parent message's ETag. This read therefore serves
		// both purposes: validate Graph retained the requested From identity and persist the
		// final post-attachment concurrency revision rather than the stale POST/PATCH response.
		var saved =
			await ThrottleAwareAsync(
				() =>
					client.Me.Messages[draftId]
						.GetAsync(
							configuration =>
								configuration.QueryParameters.Select = ["id", "from"],
							ct
						)
			) ?? throw new InvalidOperationException("Graph did not return the saved draft.");
		var actual = saved.From?.EmailAddress?.Address;
		if (
			!string.IsNullOrWhiteSpace(fromAddress)
			&& !string.Equals(actual, fromAddress, StringComparison.OrdinalIgnoreCase)
		)
		{
			throw new ProviderDraftRejectedException(
				$"Microsoft Graph cannot preserve the selected From identity '{fromAddress}' on this draft.",
				DraftResultOf(saved, draftId)
			);
		}
		return saved;
	}

	private static async Task<DraftResult> DraftResultAfterPartialUpdateAsync(
		GraphServiceClient client,
		string draftId,
		CancellationToken ct
	)
	{
		var saved = await ThrottleAwareAsync(
			() => client.Me.Messages[draftId].GetAsync(
				configuration => configuration.QueryParameters.Select = ["id"],
				ct
			)
		) ?? throw new InvalidOperationException("Graph did not return the partially updated draft.");
		return DraftResultOf(saved, draftId);
	}

	private static DraftResult DraftResultOf(GraphMessage? message, string? fallbackId = null) =>
		new(
			message?.Id ?? fallbackId ?? throw new InvalidOperationException("Graph did not return a draft id."),
			message?.AdditionalData?.TryGetValue("@odata.etag", out var etag) == true ? etag as string : null,
			message?.Id ?? fallbackId
		);
}
