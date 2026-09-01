using Microsoft.Graph;
using Microsoft.Graph.Me.Messages.Item.Move;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Abstractions;
using MyloMail.Api.Domain;
using MyloMail.Api.Errors;
using MyloMail.Api.Providers.Contracts;
using DomainMailbox = MyloMail.Api.Domain.Mailbox;
using GraphMessage = Microsoft.Graph.Models.Message;

namespace MyloMail.Api.Providers.Graph;

public sealed partial class GraphMailProvider
{
	public async Task<RawMessageResult> FetchRawMessageAsync(
		Account account,
		MessageOccurrenceRef occurrence,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		await using var stream = await client.Me.Messages[occurrence.ProviderOccurrenceId].Content.GetAsync(
			null,
			ct
		);
		if (stream is null)
		{
			throw new InvalidOperationException("Graph returned no raw MIME stream.");
		}

		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, ct);
		return new RawMessageResult(buffer.ToArray());
	}

	public Task<AttachmentConstraints> GetAttachmentConstraintsAsync(Account account, CancellationToken ct) =>
		Task.FromResult(
			new AttachmentConstraints(
				150 * 1024 * 1024,
				null,
				account.AttachmentSizeLimitOverride,
				account.AttachmentSizeLimitOverride is null
			)
		);

	public async Task<BatchResult> SetFlagsAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		FlagUpdate update,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var message = new GraphMessage();
				if (update.IsRead is bool read)
				{
					message.IsRead = read;
				}
				if (update.IsFlagged is bool flagged)
				{
					message.Flag = new FollowupFlag
					{
						FlagStatus = flagged ? FollowupFlagStatus.Flagged : FollowupFlagStatus.NotFlagged,
					};
				}

				var request = client.Me.Messages[reference.ProviderOccurrenceId].ToPatchRequestInformation(
					message
				);
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var response = await client.Batch.PostAsync(batch, ct);
			var statuses = await response.GetResponsesStatusCodesAsync();
			foreach (var (id, reference) in steps)
			{
				var status = statuses[id];
				items.Add(
					status is >= System.Net.HttpStatusCode.OK and < System.Net.HttpStatusCode.MultipleChoices
						? new BatchItemResult(reference.MessageId, reference.MailboxId, true, null, [])
						: Failed(reference, status)
				);
			}
		}

		return new BatchResult(items);
	}

	public async Task<BatchResult> MoveMessagesAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		DomainMailbox target,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var destinationId = ProviderMailboxId(target);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var request = client.Me.Messages[reference.ProviderOccurrenceId].Move.ToPostRequestInformation(
					new MovePostRequestBody { DestinationId = destinationId }
				);
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var response = await client.Batch.PostAsync(batch, ct);
			foreach (var (id, reference) in steps)
			{
				using var itemResponse = await response.GetResponseByIdAsync(id);
				if (!itemResponse.IsSuccessStatusCode)
				{
					items.Add(Failed(reference, itemResponse.StatusCode));
					continue;
				}

				var json = await itemResponse.Content.ReadAsStringAsync(ct);
				using var document = System.Text.Json.JsonDocument.Parse(json);
				if (!document.RootElement.TryGetProperty("id", out var idProperty))
				{
					throw new InvalidOperationException("Graph move returned no immutable id.");
				}

				var providerId = idProperty.GetString()
					?? throw new InvalidOperationException("Graph move returned an empty immutable id.");
				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[
							new OccurrenceChange(reference.MailboxId, null, Removed: true),
							new OccurrenceChange(target.Id, providerId, Removed: false),
						]
					)
				);
			}
		}

		return new BatchResult(items);
	}

	/// <summary>
	/// Moves the occurrence with no destination <see cref="DomainMailbox"/> the way
	/// <see cref="MoveMessagesAsync"/> has one, since well-known folder names (<c>deleteditems</c>
	/// here) are valid Graph folder ids on their own — the same well-known-id shortcut
	/// <see cref="MoveMailboxAsync"/> uses for the mailbox root.
	/// </summary>
	public async Task<BatchResult> MoveToTrashAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		const string TrashFolderId = "deleteditems";
		var client = await ClientAsync(account, ct);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var group in refs.Chunk(20))
		{
			var batch = new BatchRequestContentCollection(client.RequestAdapter, 20);
			var steps = new Dictionary<string, MessageOccurrenceRef>();
			foreach (var reference in group)
			{
				var request = client.Me.Messages[reference.ProviderOccurrenceId].Move.ToPostRequestInformation(
					new MovePostRequestBody { DestinationId = TrashFolderId }
				);
				SetImmutableIdPreference(request);
				steps[await batch.AddBatchRequestStepAsync(request)] = reference;
			}

			var response = await client.Batch.PostAsync(batch, ct);
			foreach (var (id, reference) in steps)
			{
				using var itemResponse = await response.GetResponseByIdAsync(id);
				if (!itemResponse.IsSuccessStatusCode)
				{
					items.Add(Failed(reference, itemResponse.StatusCode));
					continue;
				}

				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[new OccurrenceChange(reference.MailboxId, null, Removed: true)]
					)
				);
			}
		}

		return new BatchResult(items);
	}

	/// <summary>
	/// Graph messages have exactly one mailbox membership (§2:
	/// <see cref="MyloMail.Api.Providers.ProviderCapabilities.SupportsMultipleMailboxMembership"/>
	/// is false), so there is no "still exists elsewhere" outcome removal from that one mailbox
	/// could mean — the same divergence IMAP resolves by treating this the same as deletion.
	/// </summary>
	public Task<BatchResult> RemoveFromMailboxAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	) => DeletePermanentlyAsync(account, refs, ct);

	public async Task<BatchResult> DeletePermanentlyAsync(
		Account account,
		IReadOnlyList<MessageOccurrenceRef> refs,
		CancellationToken ct
	)
	{
		var client = await ClientAsync(account, ct);
		var items = new List<BatchItemResult>(refs.Count);
		foreach (var reference in refs)
		{
			try
			{
				await client.Me.Messages[reference.ProviderOccurrenceId].DeleteAsync(null, ct);
				items.Add(
					new BatchItemResult(
						reference.MessageId,
						reference.MailboxId,
						true,
						null,
						[new OccurrenceChange(reference.MailboxId, null, Removed: true)]
					)
				);
			}
			catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
			{
				items.Add(NotFound(reference));
			}
		}

		return new BatchResult(items);
	}

	private static BatchItemResult NotFound(MessageOccurrenceRef reference) =>
		new(
			reference.MessageId,
			reference.MailboxId,
			false,
			new MutationProblemDetails
			{
				Title = "Message not found",
				Detail = "The message is no longer present in that mailbox on Microsoft Graph.",
				Category = ErrorCategory.ProviderRejected,
				ProviderCode = "NOTFOUND",
			},
			[]
		);

	private static void SetImmutableIdPreference(RequestInformation request) =>
		request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");

	private static BatchItemResult Failed(
		MessageOccurrenceRef reference,
		System.Net.HttpStatusCode status
	) =>
		status == System.Net.HttpStatusCode.NotFound
			? NotFound(reference)
			: new BatchItemResult(
				reference.MessageId,
				reference.MailboxId,
				false,
				new MutationProblemDetails
				{
					Title = "Graph mutation was rejected",
					Detail = $"Microsoft Graph returned HTTP {(int)status} for this item.",
					Category = ErrorCategory.ProviderRejected,
					ProviderCode = ((int)status).ToString(),
				},
				[]
			);
}
